using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Wasmtime;

namespace BlastGb.Runtime;

/// <summary>Registers read from the emulator's public CPU API.</summary>
public sealed record CpuRegisters(int A, int F, int AF, int BC, int DE, int HL, int SP, int PC)
{
    public int this[string register] => register.ToLowerInvariant() switch
    {
        "a" => A,
        "f" => F,
        "af" => AF,
        "bc" => BC,
        "de" => DE,
        "hl" => HL,
        "sp" => SP,
        "pc" => PC,
        _ => throw new ArgumentException($"Unknown CPU register {register}.", nameof(register))
    };
}

public sealed record DebugSnapshot(double Ticks, CpuRegisters Registers, int[] Breakpoints, string StopReason);

/// <summary>
/// An isolated, disposable Game Boy CPU. The embedded, unmodified binjgb WebAssembly
/// executes directly in Wasmtime; only its public C bus and CPU exports are used.
/// Instances are not thread-safe. ROM bytes are copied into private emulator memory.
/// </summary>
public sealed class EmulatorSession : IDisposable
{
    public const string EngineSha256 = "52c165069441a09180b4bd63464318aa7b861b1d36e0d05e8047b48619fd2f0b";
    public static RuntimeProvenance Provenance { get; } = new("binjgb", EngineSha256);
    public const int ClockRate = 4_194_304;
    public const int FrameTicks = 70_224;
    // Compile once for the process. Each session owns a separate disposable store;
    // only immutable compiled code is shared between sessions.
    private static readonly Lazy<CompiledEmulator> Compiled = new(() => new());
    private readonly Store store;
    private readonly Linker linker;
    private readonly Instance instance;
    private readonly Memory memory;
    private readonly int romPointer;
    private readonly int emulator;
    private readonly int joypad;
    private readonly int cartridgeType;
    private readonly Func<int, double> getTicks;
    private readonly Func<int, double, int> runUntil;
    private readonly Func<int, int>[] getRegisters;
    private readonly Func<int, int, int> readMemory;
    private readonly Action<int, int, int> writeMemory;
    private readonly Func<int, int> framebufferPointer;
    private readonly List<string> diagnostics = [];
    private readonly Dictionary<int, List<byte>> diagnosticLines = new() { [1] = [], [2] = [] };
    private readonly Dictionary<string, Action<int, int>> buttons;
    private HashSet<int> breakpoints = [];
    private string stopReason = "ready";
    private int consecutiveEventOnlyStops;
    private bool disposed;

    private sealed class CompiledEmulator
    {
        public Engine Engine { get; } = new();
        public Module Module { get; }
        public CompiledEmulator()
        {
            try
            {
                using var stream = typeof(EmulatorSession).Assembly.GetManifestResourceStream("BlastGb.Runtime.binjgb.wasm")
                    ?? throw new InvalidOperationException("The embedded binjgb emulator is missing.");
                Module = Module.FromStream(Engine, "binjgb", stream);
            }
            catch
            {
                Engine.Dispose();
                throw;
            }
        }
    }

    public EmulatorSession(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        if (rom.Length is < 0x150 or > 8 * 1024 * 1024)
            throw new ArgumentException("Choose a Game Boy ROM between 336 bytes and 8 MB.", nameof(rom));
        cartridgeType = rom[0x147];
        var compiled = Compiled.Value;
        store = new Store(compiled.Engine);
        try
        {
            linker = new Linker(compiled.Engine);
            // The release's Emscripten import module/export names are deliberately fixed
            // to this checked-in binary. No WASI filesystem, networking, or clocks are exposed.
            linker.DefineFunction("a", "c", (Func<int, int>)((int _) => throw new InvalidOperationException("The emulator exhausted its fixed WebAssembly memory.")));
            linker.DefineFunction("a", "b", (int code) => throw new InvalidOperationException($"The emulator exited with status {code}."));
            linker.DefineFunction("a", "a", (Caller caller, int fd, int iov, int count, int written) => WriteDiagnostic(caller, fd, iov, count, written));
            instance = linker.Instantiate(store, compiled.Module);
            memory = instance.GetMemory("d") ?? throw new InvalidOperationException("The emulator did not export its memory.");
            instance.GetAction("e")!(); // Emscripten static constructors.
            getTicks = instance.GetFunction<int, double>("M")!;
            runUntil = instance.GetFunction<int, double, int>("N")!;
            // Public C exports in A, F, BC, DE, HL, SP, PC order.
            getRegisters = new[] { "q", "u", "r", "s", "t", "v", "p" }.Select(name => instance.GetFunction<int, int>(name)!).ToArray();
            readMemory = instance.GetFunction<int, int, int>("z")!;
            writeMemory = instance.GetAction<int, int, int>("A")!;
            framebufferPointer = instance.GetFunction<int, int>("da")!;
            buttons = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["up"] = "X",
                ["down"] = "Y",
                ["left"] = "Z",
                ["right"] = "_",
                ["B"] = "$",
                ["A"] = "aa",
                ["start"] = "ba",
                ["select"] = "ca"
            }.ToDictionary(pair => pair.Key, pair => instance.GetAction<int, int>(pair.Value)!, StringComparer.Ordinal);
            romPointer = instance.GetFunction<int, int>("f")!(rom.Length);
            if (romPointer == 0) throw new InvalidOperationException("The emulator could not allocate cartridge memory.");
            rom.CopyTo(memory.GetSpan(romPointer, rom.Length));
            emulator = instance.GetFunction<int, int, int, int, int, int>("L")!(romPointer, rom.Length, 48_000, 2048, 0);
            if (emulator == 0) throw new InvalidOperationException("The emulator rejected this ROM. Check its cartridge type and size.");
            joypad = instance.GetFunction<int>("H")!();
            if (joypad == 0) throw new InvalidOperationException("The emulator could not allocate a joypad.");
            instance.GetAction<int, int>("Q")!(emulator, joypad);
        }
        catch
        {
            // Disposing the isolated store releases all guest allocations even if startup traps.
            linker?.Dispose();
            store.Dispose();
            throw;
        }
    }

    public IReadOnlyList<string> Diagnostics => diagnostics.AsReadOnly();
    public double Ticks { get { ThrowIfDisposed(); return getTicks(emulator); } }
    public CpuRegisters Registers
    {
        get
        {
            ThrowIfDisposed();
            int a = getRegisters[0](emulator), f = getRegisters[1](emulator);
            return new(a, f, (a << 8) | f, getRegisters[2](emulator), getRegisters[3](emulator),
                getRegisters[4](emulator), getRegisters[5](emulator), getRegisters[6](emulator));
        }
    }

    public DebugSnapshot Snapshot() => new(Ticks, Registers, breakpoints.Order().ToArray(), stopReason);
    public byte[] Framebuffer()
    {
        ThrowIfDisposed();
        return memory.GetSpan(framebufferPointer(emulator), 160 * 144 * 4).ToArray();
    }
    public string FramebufferSha256() => Convert.ToHexStringLower(SHA256.HashData(Framebuffer()));

    public byte[] ReadMemory(int address, int length = 1, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMemoryRange(address, length);
        var bytes = new byte[length];
        for (int index = 0; index < length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes[index] = (byte)readMemory(emulator, address + index);
        }
        return bytes;
    }

    /// <summary>Writes temporary mapped RAM/IO/mapper state; never alters cartridge bytes.</summary>
    public void WriteMemory(int address, ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMemoryRange(address, bytes.Length);
        for (int index = 0; index < bytes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writeMemory(emulator, address + index, bytes[index]);
        }
    }

    /// <summary>Loads the engine's exact raw battery-RAM image. Timer/RTC and unusual save formats are unsupported.</summary>
    public void ImportBatterySave(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBatteryCartridge();
        int file = NewBatteryFile();
        try
        {
            int length = instance.GetFunction<int, int>("ma")!(file);
            if (data.Length != length) throw new ArgumentException($"Battery save must contain exactly {length} bytes for this cartridge.", nameof(data));
            data.CopyTo(memory.GetSpan(instance.GetFunction<int, int>("la")!(file), length));
            cancellationToken.ThrowIfCancellationRequested();
            if (instance.GetFunction<int, int, int>("m")!(emulator, file) != 0)
                throw new InvalidOperationException("The emulator rejected the battery save.");
        }
        finally { DeleteBatteryFile(file); }
    }

    /// <summary>Copies raw battery RAM; no host file is read or written.</summary>
    public byte[] ExportBatterySave(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBatteryCartridge();
        int file = NewBatteryFile();
        try
        {
            if (instance.GetFunction<int, int, int>("n")!(emulator, file) != 0)
                throw new InvalidOperationException("The emulator could not export battery RAM.");
            int length = instance.GetFunction<int, int>("ma")!(file);
            cancellationToken.ThrowIfCancellationRequested();
            return memory.GetSpan(instance.GetFunction<int, int>("la")!(file), length).ToArray();
        }
        finally { DeleteBatteryFile(file); }
    }

    private void ValidateBatteryCartridge()
    {
        if (cartridgeType is 0x0f or 0x10)
            throw new ArgumentException("Battery saves for timer cartridges are unsupported because the engine's public RAM format omits RTC state.");
        if (cartridgeType is not (0x03 or 0x06 or 0x09 or 0x13 or 0x1b or 0x1e))
            throw new ArgumentException("Battery saves require a supported non-timer MBC1, MBC2, ROM+RAM, MBC3, or MBC5 battery cartridge.");
    }

    private int NewBatteryFile()
    {
        int file = instance.GetFunction<int, int>("ja")!(emulator);
        if (file == 0) throw new InvalidOperationException("The emulator could not allocate a battery-save buffer.");
        int length = instance.GetFunction<int, int>("ma")!(file);
        int pointer = instance.GetFunction<int, int>("la")!(file);
        if (length is < 1 or > 131072 || pointer == 0)
        {
            DeleteBatteryFile(file);
            throw new InvalidOperationException("The cartridge has no supported battery RAM.");
        }
        return file;
    }

    private void DeleteBatteryFile(int file)
    {
        // file_data_delete frees its payload, but the public API leaves the FileData allocation to the caller.
        try { instance.GetAction<int>("g")!(file); }
        finally { instance.GetAction<int>("h")!(file); }
    }

    public void SetButton(string button, bool pressed)
    {
        ThrowIfDisposed();
        if (!buttons.TryGetValue(button, out var set)) throw new ArgumentException($"Unknown button {button}.", nameof(button));
        set(emulator, pressed ? 1 : 0);
    }

    public void SetBreakpoints(IEnumerable<int> addresses)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(addresses);
        var next = addresses.ToHashSet();
        foreach (int address in next) ValidateMemoryRange(address, 1);
        breakpoints = next;
    }

    public DebugSnapshot Advance(double ticks, Action? frame = null, Action? audio = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(ticks) || ticks < 1 || ticks > ClockRate)
            throw new ArgumentException("Advance between 1 and 4,194,304 clock ticks at a time.", nameof(ticks));
        double current = Ticks, target = Math.Ceiling(current + ticks);
        stopReason = "target";
        while (current < target)
        {
            // Native breakpoint exports in this release are no-ops. Upstream's single
            // step is run_until(current + 1), including HALT/DMA hardware steps.
            double previous = current;
            int events = RunUntil(breakpoints.Count > 0 ? Math.Min(current + 1, target) : target, cancellationToken);
            if ((events & 1) != 0) frame?.Invoke();
            if ((events & 2) != 0) audio?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            current = Ticks;
            if ((events & 8) != 0 || current > previous && breakpoints.Contains(getRegisters[6](emulator)))
            {
                stopReason = "breakpoint";
                break;
            }
        }
        return Snapshot();
    }

    internal int RunUntil(double targetTicks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double previous = Ticks;
        int events = runUntil(emulator, targetTicks);
        cancellationToken.ThrowIfCancellationRequested();
        if ((events & 16) != 0)
            throw new InvalidOperationException($"The ROM reached an invalid CPU instruction at PC 0x{getRegisters[6](emulator):x}.");
        double current = Ticks;
        if (current == previous && (events & 3) != 0)
        {
            // Audio/video can become pending at the end of the previous hardware
            // step. Deliver those events before retrying without charging a CPU
            // step. Bound retries so a broken guest cannot spin on event delivery.
            if (++consecutiveEventOnlyStops > 8)
                throw new InvalidOperationException("The emulator repeatedly emitted events without advancing its clock.");
        }
        else
        {
            if (current <= previous) throw new InvalidOperationException("The emulator stopped advancing before the requested execution budget.");
            consecutiveEventOnlyStops = 0;
        }
        return events;
    }

    public static void ValidateMemoryRange(int address, int length)
    {
        if (address is < 0 or > 0xffff || length < 1 || length > 0x10000 - address)
            throw new ArgumentException("Memory access must stay within $0000–$FFFF.");
    }

    private int WriteDiagnostic(Caller caller, int fd, int iov, int count, int written)
    {
        var guest = caller.GetMemory("d") ?? throw new InvalidOperationException("Missing emulator memory in diagnostic callback.");
        if (!diagnosticLines.TryGetValue(fd, out var line)) return 8; // WASI EBADF.
        int total = 0;
        for (int index = 0; index < count; index++)
        {
            var vector = guest.GetSpan(checked(iov + index * 8), 8);
            int pointer = BinaryPrimitives.ReadInt32LittleEndian(vector);
            int length = BinaryPrimitives.ReadInt32LittleEndian(vector[4..]);
            foreach (byte value in guest.GetSpan(pointer, length))
            {
                if (value is 0 or 10)
                {
                    if (diagnostics.Count < 256) diagnostics.Add(Encoding.UTF8.GetString(line.ToArray()));
                    else if (diagnostics.Count == 256) diagnostics.Add("Further emulator diagnostics omitted.");
                    line.Clear();
                }
                else if (line.Count < 4096) line.Add(value);
            }
            total = checked(total + length);
        }
        BinaryPrimitives.WriteInt32LittleEndian(guest.GetSpan(written, 4), total);
        return 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            instance.GetAction<int>("o")!(emulator);
            instance.GetAction<int>("I")!(joypad);
            instance.GetAction<int>("h")!(romPointer);
        }
        finally
        {
            linker.Dispose();
            store.Dispose();
        }
    }
}
