; Original minimal Game Boy program: write $2A to work RAM, then loop.
; The project build repairs header and global checksums.
SECTION "Cartridge", ROM0[$0000]
    ds $100, $00
    db $00, $c3, $50, $01, $ce, $ed, $66, $66, $cc, $0d, $00, $0b, $03, $73, $00, $83
    db $00, $0c, $00, $0d, $00, $08, $11, $1f, $88, $89, $00, $0e, $dc, $cc, $6e, $e6
    db $dd, $dd, $d9, $99, $bb, $bb, $67, $63, $6e, $0e, $ec, $cc, $dd, $dc, $99, $9f
    db $bb, $b9, $33, $3e, $47, $42, $20, $55, $54, $49, $4c, $20, $44, $45, $4d, $4f
    db $00, $00, $00, $00, $00, $00, $00, $00, $00, $00, $01, $00, $00, $ba, $70, $87

Start:
    di
    ld sp, $dfff
    ld a, $2a
    ld [$c000], a
.loop:
    jr .loop

    ds $4000 - @, $00
SECTION "Second ROM bank", ROMX[$4000], BANK[1]
    ds $4000, $00
