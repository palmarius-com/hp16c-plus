; HP-16C+: word size extension to 128 bits, Nut code.
; Build: tools/build_plus.py.
;
; Notes on the Nut CPU used below:
; - a conditional branch tests the carry produced by the instruction
;   just before it; rtn produces no carry, so subroutines return results
;   in registers, never in the carry.
; - "?nc goto/golong" after an instruction that cannot set carry is an
;   unconditional jump.
; - "c=reg 0" is c=data: it reads the register selected last (by sel
;   ram or any c=reg n), not register 0 of the chip; select again (c=0 w
;   ; sel ram) after touching another one. "reg=c 0" is not data=c: it
;   writes register 0 of the selected chip (address & ~0xF); write a
;   computed address with data=c.

; Original routines (see re/16c.sym)
ABS_X       equ $060b       ; C <- |X| low 56 bits, A.x <- bits above, N <- X
WSIZE_CONT  equ $0e84       ; original F_WSIZE after its call to ABS_X
STAT_W      equ $06d4       ; STATUS: word size from RAM 6 -> C.x binary
STAT_DEC    equ $06ea       ; STATUS: binary -> decimal digits
STAT_CONT   equ $1371       ; STATUS after its first call
SHOW_TEXT   equ $01c2       ; show the text in C (digits 12-3 -> LCD 1-10)
MEMRES_CONT equ $0374       ; memory reset after "ldi $004"
RENDER      equ $0986       ; display renderer (A window digits, G count)
DIGIT_INT   equ $0b67       ; original integer digit entry
DIGIT_FLT   equ $0c87       ; original float digit entry
MAIN_DISP   equ $0094       ; main loop after a key (display)
BSP_CLX     equ $0c40       ; BSP outside entry: clear X
BSP_ENTRY   equ $1403       ; BSP in digit entry (per base)
FORMAT_CONT equ $11ef       ; integer display formatter after the hook
ERROR2      equ $0687       ; pt=2, error display
DISP_CONT   equ $087e       ; dispatcher after the hook (lc c ...)
DISP_EPI    equ $0882       ; dispatcher epilogue after "push target"
SAVE_LASTX  equ $039b       ; LSTx <- X (RAM 4, RAM 6 digits 1-0); keeps A, C
MAIN_NOLX   equ $008b       ; main loop after its LSTx save (C = X, A.x = hi)
STACK_DROP  equ $0e51       ; X <- C, Y <- Z, Z <- T (bits 0-63); keeps A
STACK_LIFT  equ $0e68       ; T <- Z, Z <- Y, Y <- X (bits 0-63); keeps A, C
FLAGS_CG    equ $03c2       ; flag 4 <- s10, flag 5 <- s13 (RAM select 0)
FLAG_C      equ $03b2       ; flag 4 <- s10
FLAG_G      equ $03bf       ; flag 5 <- s13
ERR_N       equ $105f       ; "Error n", n = pt
CLR_ENTRY   equ $0394       ; clear the digit entry state (RAM 6)
REG_WRITE   equ $1128       ; X -> storage register (original)
RCL_CONT    equ $08fc       ; RCL n after its CLR_ENTRY call
STO_CONT    equ $0916       ; STO n after its REG_WRITE call
MAIN_LIFT   equ $0090       ; main loop: stack lift on, entry off
SKIP        equ $0291       ; skip the next program step
COND_END    equ $0f79       ; conditionals' ending (X to C, A.x; 0x008B)
FLOAT_INT   equ $1758       ; FLOAT n: integer Y * 2^X to float (64 bits)
MEM_K       equ $01f6       ; MEM after its sel ram: k from RAM 6
MEM_DIV     equ $01ff       ; MEM: free nibbles (A.x) / k (C.x)

W1_WSIZE    equ $282        ; dispatch table word 1 of WSIZE (0x0E82)

; Extension RAM (second chip), selected with C.x = $010 and "sel ram":
;   reg 0 CTRL: digits 1-0 W (hex), digit 2 = 1 in plus mode
;   reg 1..5 X, Y, Z, T, LSTx bits 64-119
;   reg 6 bits 120-127: digits 1-0 X, 3-2 Y, 5-4 Z, 7-6 T, 9-8 LSTx;
;         digit 10 I bits 124-127
;   reg 7 I bits 68-123

; ------------------------------------------------------------- hooks
        org $087c
        ?nc golong P_DISP           ; was: c=0 w ; pt= 2

        org $136f
        ?nc golong P_STATUS         ; was: ?nc gosub $06d4 (STATUS)

        org $0372
        ?nc golong P_MEMRESET       ; was: ldi $004 (memory lost, "Pr Error")

        org $11ed
        ?nc golong P_FORMAT         ; was: s=0 13 ; set hex (integer formatter)

        org $0c84
        ?nc golong P_DIGIT          ; was: ? s=1 12 ; ?nc golong $0b67 (word 1)

        org $1400
        ?nc golong P_BSP

        org $08fa
        ?nc golong P_RCL            ; was: ?nc gosub CLR_ENTRY (RCL n)

        org $0914
        ?nc golong P_STO            ; was: ?nc gosub REG_WRITE (STO n)

        org $097f
        ?nc golong P_FLOAT          ; was: ?nc golong 1758 (FLOAT n)

        org $01f4
        ?nc golong P_MEM            ; was: c=0 w ; sel ram (MEM)            ; was: ? s=1 7 ; ?nc golong $0c40 (word 1)

; ------------------------------------------------------- second ROM
        org $2000

; P_DISP: dispatcher hook. In: A.x = word 1 of the table entry, B.x =
; word 2, return address 0x0089 already pushed, RAM select 0x000.
; Plus mode off and not WSIZE: original path, nothing changed.
; Otherwise push our handler and continue with the original epilogue.
P_DISP:
        ldi $010
        sel ram
        c=reg 0                     ; CTRL
        pt= 2
        ? c#0   p                   ; plus mode?
        ?c goto PD_PLUS
        ldi W1_WSIZE
        ? a#c   x
        ?nc goto PD_WSIZE
PD_ORIG:
        c=0     w                   ; original path: RAM select back
        sel ram
        pt= 2                       ; displaced: c=0 w ; pt= 2
        ?nc golong DISP_CONT
PD_PLUS:
        ldi W1_WSIZE
        ? a#c   x
        ?nc goto PD_WSIZE
        ?nc gosub STACK_KEY         ; C <> 0: a stack key, extension moved
        ? c#0   w
        ?c goto PD_ORIG             ; low 64 bits: the original routine
        ?nc gosub MODE_KEY          ; C <> 0: mode-only key (complement,
        ? c#0   w                   ;  base): the original routine as is
        ?c goto PD_ORIG
        c=reg 0                     ; any other function: new code; word 1
        rcr 3                       ;  -> CTRL digits 5-3 for P_FUNC
        ac ex   x
        rcr 11
        reg=c 0
        c=0     w
        sel ram
        pt= 3
        lc 2
        lc 0
        lc 8
        lc 0                        ; $2080 P_FN
        ?nc goto PD_PUSH
PD_WSIZE:
        c=0     w
        sel ram
        pt= 3
        lc 2
        lc 1
        lc 0
        lc 0                        ; $2100 P_WSIZE
PD_PUSH:
        rcr 11                      ; C[6..3] = address
        stk=c
        ?nc golong DISP_EPI

; ------------------------------------------------ functions (stage 3+)
        org $2080
P_FN:
        ?nc golong P_FUNC

; ---------------------------------------------------------- WSIZE
        org $2100
; P_WSIZE: entered like an original routine (rtn from the dispatcher
; epilogue, return address 0x0089 on the stack).
P_WSIZE:
        ?nc gosub X_EXT_OK          ; C = 0: X's extension not usable
        ? c#0   w
        ?nc goto PW_ERR
        ?nc gosub ABS_X             ; C = |X| low, A.x = bits above
        ? a#0   x
        ?c goto PW_ERR
        a=c     w
        c=0     w
        ldi $081                    ; 129
        ? a<c   w
        ?nc goto PW_ERR             ; |X| > 128
        ldi $041
        ? a<c   w
        ?c goto PW_LOW              ; |X| <= 64 (0 means 64)
        ; 65 <= n <= 128: plus mode
        ?nc gosub EXT_LASTX_DROP    ; L.ext <- X.ext; X,Y,Z.ext <- Y,Z,T.ext
        ?nc gosub ABS_X             ; C = n again
        ?nc gosub SET_PLUS_W        ; CTRL <- n, plus on
        ?nc gosub IWSIZE            ; I to max(68, W) bits
        c=0     w
        sel ram                     ; (the original reads RAM 7)
        ?nc gosub ABS_X             ; registers as the original expects
        c=0     w
        ldi $040                    ; low 64 bits: original WSIZE with 64
        a=0     x
        ?nc golong WSIZE_CONT
PW_LOW:
        ?nc gosub EXT_CLEAR         ; plus mode off, extension 0
        ?nc gosub ABS_X
        ?nc golong WSIZE_CONT
PW_ERR:
        ?nc golong ERROR2

; X_EXT_OK: C <> 0 if plus mode is off or X's bits 64-127 are zero;
; C = 0 otherwise (negative or large X in plus mode: stage 3).
; Uses A, C; keeps B, M, N. RAM select back to 0x000.
X_EXT_OK:
        ldi $010
        sel ram
        c=reg 0
        pt= 2
        ? c#0   p
        ?c goto XO_PLUS
XO_TRUE:
        c=0     w
        sel ram
        c=c+1   w
        rtn
XO_PLUS:
        c=reg 1                     ; X bits 64-119
        ? c#0   w
        ?c goto XO_FALSE
        c=reg 6
        pt= 1
        ? c#0   wp                  ; X bits 120-127
        ?nc goto XO_TRUE
XO_FALSE:
        c=0     w
        sel ram
        rtn

; EXT_LASTX_DROP: LSTx.ext <- X.ext, then X.ext <- Y.ext, Y <- Z, Z <- T
; (T unchanged). Uses A, B, C; keeps M, N. RAM select back to 0x000.
EXT_LASTX_DROP:
        ldi $010
        sel ram
        c=reg 1
        reg=c 5                     ; L <- X (bits 64-119)
        c=reg 2
        reg=c 1                     ; X <- Y
        c=reg 3
        reg=c 2                     ; Y <- Z
        c=reg 4
        reg=c 3                     ; Z <- T
        c=reg 6                     ; bits 120-127, 2 nibbles per register
        bc ex   w                   ; B = old (no "b=c" on the Nut)
        c=b     w
        rcr 2                       ; C: digits 5-0 = old Y, Z, T
        a=c     w
        c=b     w
        pt= 5
        ac ex   wp                  ; C digits 5-0 = Y, Z, T (new X, Y, Z)
        bc ex   w                   ; B = new partial, C = old
        rcr 6                       ; C digits 9-8 = old X
        a=c     w
        c=b     w
        pt= 9
        ac ex   p
        pt= 8
        ac ex   p                   ; digits 9-8 = old X (new LSTx)
        reg=c 6
        c=0     w
        sel ram
        rtn

; SET_PLUS_W: C.x = n (65..128): CTRL <- W = n, plus mode on.
; Uses A, C. RAM select back to 0x000. (Truncation of the extension to
; n bits: next step.)
SET_PLUS_W:
        a=c     w
        ?nc gosub SEL10
        c=0     w
        pt= 2
        lc 1                        ; digit 2: plus mode on (pt now 1)
        ac ex   wp                  ; digits 1-0 = n
        reg=c 0
        c=0     w
        sel ram
        ?nc goto EXT_TRUNC          ; (tail call) cut X..T extensions to W

; EXT_CLEAR: plus mode off, extension words 0, I's too (I keeps its 68
; bits in 0xFD/0xFE).
; Uses C. RAM select back to 0x000.
EXT_CLEAR:
        ?nc gosub SEL10
        c=0     w
        reg=c 0
        reg=c 1
        reg=c 2
        reg=c 3
        reg=c 4
        reg=c 5
        reg=c 6
        reg=c 7
        sel ram                     ; C = 0
        rtn

; EXT_TRUNC: truncate the X, Y, Z, T extensions to k = W - 64 bits (W
; from CTRL). LSTx is not truncated (as the original). Uses A, B, C.
; RAM select back to 0x000. Hex mode.
EXT_TRUNC:
        set hex
        ?nc gosub SEL10
        c=reg 0                     ; CTRL: W in digits 1-0
        a=0     w
        pt= 1
        ac ex   wp                  ; A.x = W
        c=0     w
        ldi $040
        a=a-c   x                   ; A.x = k (1..64)
        b=0     w                   ; B = mask for bits 120-127 (k > 56)
        c=0     w
        ldi $038
        ? a<c   x                   ; k < 56 ?
        ?c goto ET_LO
        a=a-c   x                   ; k - 56 (0..8)
        c=0     w
ET_H:
        ? a#0   x
        ?nc goto ET_H1
        c=c+c   w
        c=c+1   w
        a=a-1   x
        ?nc goto ET_H
ET_H1:
        bc ex   w                   ; B = 2^(k-56) - 1
        c=0     w
        c=c-1   w                   ; mask for bits 64-119: all ones
        a=c     w
        ?nc goto ET_APPLY
ET_LO:
        c=0     w
ET_L:
        ? a#0   x
        ?nc goto ET_L1
        c=c+c   w
        c=c+1   w
        a=a-1   x
        ?nc goto ET_L
ET_L1:
        a=c     w                   ; A = 2^k - 1, B = 0
ET_APPLY:
        c=reg 1
        c=c&a
        reg=c 1
        c=reg 2
        c=c&a
        reg=c 2
        c=reg 3
        c=c&a
        reg=c 3
        c=reg 4
        c=c&a
        reg=c 4
        c=0     w                   ; reg 6 mask: B's 2 nibbles at X, Y,
        c=c-1   w                   ;  Z, T (digits 7-0), ones elsewhere
        pt= 1
        c=b     wp
        rcr 12
        c=b     wp
        rcr 12
        c=b     wp
        rcr 12
        c=b     wp
        a=c     w
        c=reg 6
        c=c&a
        reg=c 6
        c=0     w
        sel ram
        rtn

; STACK_KEY: plus mode, A.x = word 1, RAM select 0x010. Stack keys move
; the extension words and return C <> 0; others return C = 0 (no change).
; Keeps A; uses B, C; G saved in CTRL digits 13-12 and restored.
STACK_KEY:
        c=reg 0
        pt= 12
        c=g
        reg=c 0                     ; save G
        ldi $021
        ? a#c   x
        ?nc golong SK_ENTER
        ldi $007
        ? a#c   x
        ?nc golong SK_SWAP
        ldi $004
        ? a#c   x
        ?nc golong SK_RDN
        ldi $000
        ? a#c   x
        ?nc golong SK_RUP
        ldi $042
        ? a#c   x
        ?nc golong SK_CLX
        ldi $018
        ? a#c   x
        ?nc golong SK_LSTX
        c=0     w                   ; not a stack key
        rtn
SK_ENTER:
        ?nc gosub EXT_LIFT
        ?nc golong SK_DONE
SK_SWAP:
        c=reg 1                     ; X <-> Y
        bc ex   w
        c=reg 2
        reg=c 1
        c=b     w
        reg=c 2
        c=reg 6
        pt= 0
        g=c
        pt= 2
        c<>g
        pt= 0
        c=g
        reg=c 6
        ?nc golong SK_DONE
SK_RDN:
        c=reg 1                     ; X <- Y <- Z <- T <- X
        bc ex   w
        c=reg 2
        reg=c 1
        c=reg 3
        reg=c 2
        c=reg 4
        reg=c 3
        c=b     w
        reg=c 4
        c=reg 6
        pt= 0
        g=c
        pt= 6
        c<>g
        pt= 4
        c<>g
        pt= 2
        c<>g
        pt= 0
        c=g
        reg=c 6
        ?nc golong SK_DONE
SK_RUP:
        c=reg 4                     ; X <- T, T <- Z, Z <- Y, Y <- X
        bc ex   w
        c=reg 3
        reg=c 4
        c=reg 2
        reg=c 3
        c=reg 1
        reg=c 2
        c=b     w
        reg=c 1
        c=reg 6
        pt= 0
        g=c
        pt= 2
        c<>g
        pt= 4
        c<>g
        pt= 6
        c<>g
        pt= 0
        c=g
        reg=c 6
        ?nc golong SK_DONE
SK_CLX:
        c=0     w
        reg=c 1
        c=reg 6
        pt= 1
        c=0     wp
        reg=c 6
        ?nc golong SK_DONE
SK_LSTX:
        ? s=1 9                     ; stack lift disabled? (as the original)
        ?c goto SK_L1
        ?nc gosub EXT_LIFT
SK_L1:
        c=reg 5                     ; X <- LSTx
        reg=c 1
        c=reg 6
        pt= 8
        g=c
        pt= 0
        c=g
        reg=c 6
SK_DONE:
        c=reg 0
        pt= 12
        g=c                         ; restore G
        c=0     w
        c=c+1   w
        rtn

; MODE_KEY: A.x = word 1. C <> 0 for keys that never touch the stack:
; 1S, 2S, UNSGN, HEX, DEC, OCT, BIN, < and >, SHOW HEX/DEC/OCT/BIN, RTN,
; R/S, PSE.
; Uses C.
MODE_KEY:
        ldi $210                    ; 1S
        ? a#c   x
        ?nc go  MK_YES
        ldi $20f                    ; 2S
        ? a#c   x
        ?nc go  MK_YES
        ldi $211                    ; UNSGN
        ? a#c   x
        ?nc go  MK_YES
        ldi $200                    ; HEX
        ? a#c   x
        ?nc go  MK_YES
        ldi $1fc                    ; DEC
        ? a#c   x
        ?nc go  MK_YES
        ldi $1ff                    ; OCT
        ? a#c   x
        ?nc go  MK_YES
        ldi $1fe                    ; BIN
        ? a#c   x
        ?nc go  MK_YES
        ldi $209                    ; >  (display offset only)
        ? a#c   x
        ?nc go  MK_YES
        ldi $1f5                    ; <
        ? a#c   x
        ?nc go  MK_YES
        ldi $333                    ; SHOW HEX, DEC, OCT, BIN (display
        ? a#c   x                   ;  through the formatter hook)
        ?nc go  MK_YES
        ldi $32f
        ? a#c   x
        ?nc go  MK_YES
        ldi $332
        ? a#c   x
        ?nc go  MK_YES
        ldi $331
        ? a#c   x
        ?nc go  MK_YES
        ldi $028                    ; RTN, R/S, PSE: program control, no
        ? a#c   x                   ;  stack change
        ?nc go  MK_YES
        ldi $053
        ? a#c   x
        ?nc go  MK_YES
        ldi $064
        ? a#c   x
        ?nc go  MK_YES
        c=0     w
        rtn
MK_YES:
        c=0     w
        c=c+1   w
        rtn

; EXT_LIFT: T <- Z, Z <- Y, Y <- X (extension words). RAM select 0x010.
; Uses C, G.
EXT_LIFT:
        c=reg 3
        reg=c 4
        c=reg 2
        reg=c 3
        c=reg 1
        reg=c 2
        c=reg 6
        pt= 4
        g=c
        pt= 6
        c=g
        pt= 2
        g=c
        pt= 4
        c=g
        pt= 0
        g=c
        pt= 2
        c=g
        reg=c 6
        rtn

; P_STATUS: STATUS key. Plus mode off: original. On: the original text,
; then W replaced by the 3-digit word size (complement, -, WWW, -, flags).
P_STATUS:
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        ? c#0   p
        ?c goto PS_PLUS
        c=0     w
        sel ram
        ?nc gosub STAT_W            ; displaced call
        ?nc golong STAT_CONT
PS_PLUS:
        a=0     w
        pt= 1
        ac ex   wp                  ; A.x = W (binary)
        ?nc gosub BIN2BCD3          ; C.x = W in 3 decimal digits
        rcr 11                      ; to digits 5-3
        a=c     w
        ?nc gosub SEL10
        c=reg 0
        pt= 5
        ac ex   p
        pt= 4
        ac ex   p
        pt= 3
        ac ex   p
        reg=c 0                     ; CTRL digits 5-3 = W (scratch)
        c=0     w
        sel ram
        ?nc gosub STAT_W            ; original text, with "64"
        a=0     w
        ?nc gosub STAT_DEC
        c=-c-1  m
        rcr 6
        pt= 7
        lc b
        pt= 10
        lc b
        a=c     w
        c=reg 6
        rcr 1
        pt= 11
        a=c     p
        pt= 6
        c=0     p
        c=c+c   wp
        pt= 5
        c sr    wp
        c=c+c   wp
        pt= 4
        c sr    wp
        c=c+c   wp
        pt= 3
        c sr    wp
        c=c+c   wp
        pt= 6
        a=c     wp                  ; A = original text
        ac ex   w                   ; copy A to C (no "c=a" on the Nut)
        a=c     w
        rcr 13                      ; digit 12 <- complement (digit 11)
        pt= 12
        ac ex   p
        pt= 11
        lc b
        pt= 11
        ac ex   p                   ; digit 11 = "-"
        ?nc gosub SEL10
        c=reg 0
        rcr 9                       ; CTRL digits 5-3 -> digits 10-8
        pt= 10
        ac ex   p
        pt= 9
        ac ex   p
        pt= 8
        ac ex   p
        c=0     w
        sel ram
        ac ex   w                   ; C = text
        ?nc golong SHOW_TEXT

; BIN2BCD3: A.x = binary 0..255 -> C.x = 3 decimal digits. Uses A, B, C.
BIN2BCD3:
        set hex
        b=0     w
BB_H:
        c=0     w
        ldi $064
        ? a<c   x
        ?c goto BB_T
        a=a-c   x
        bc ex   w
        pt= 2
        c=c+1   p
        bc ex   w
        ?nc goto BB_H
BB_T:
        c=0     w
        ldi $00a
        ? a<c   x
        ?c goto BB_O
        a=a-c   x
        bc ex   w
        pt= 1
        c=c+1   p
        bc ex   w
        ?nc goto BB_T
BB_O:
        c=b     w
        pt= 0
        ac ex   p                   ; ones
        rtn

; P_FORMAT: integer display formatter (hook 0x11ED). Called by the main
; loop (0x119A), PSE and SHOW; the renderer (0x0986, or 0x0981) follows.
; Out, as the original: A = f + 8 window digits (digits 12-5), G = count
; (digits from the offset up to the top, + 1; in entry at least the
; digits typed + 1), C = RAM 6 with the count in digits 2-0, s13 = more
; digits to the left, s3 = negative (DEC). Plus mode, integer bases: all
; from the full X. Helpers nest at most 2 deep (SHOW calls this with
; 2 return levels in use). Uses A, B, C, G, M (M = X at the end).
P_FORMAT:
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        ? c#0   p                   ; plus mode?
        ?nc go  PDY_ORIG
        pt= 12
        c=g                         ; save G (in entry: digits typed + 1)
        reg=c 0                     ;  in CTRL digits 13-12
        c=0     w
        sel ram
        c=reg 6
        pt= 13
        c=c+c   s                   ; DEC (8) or FLOAT (c) -> carry
        ?nc go  PDY_NB
        ? c#0   s                   ; FLOAT: the original
        ?c go   PDY_ORIG
        ?nc gosub SIGNX             ; DEC: s3 = X negative
        s=0     3
        ? c#0   x
        ?nc go  PDY_DP
        s=1     3
PDY_DP:
        ?nc gosub DECBCD            ; A, B, RAM 4 = BCD of |X|
        ac ex   w
        m=c                         ; M = BCD digits 0-13 (M = X again later)
        ?nc gosub DEC_ND            ; A.x = decimal digits
        ?nc go  PDY_ND
PDY_NB:
        ; HEX, OCT, BIN: A.x = significant digits (at least 1), or with
        ; flag 3 the digits of the word size: ceil(W/4), ceil(W/3), W
        s=0     3
        c=0     w
        sel ram
        c=reg 6
        pt= 6
        c=c+c   p                   ; flag 3 (bit 3 of digit 6) -> carry
        ?c go   PDY_PAD
        c=reg 6
        pt= 13
        ? c#0   p
        ?c go   PDY_OBN
        ?nc gosub FULL_NDIG         ; HEX
        ?nc go  PDY_ND
PDY_OBN:
        ?nc gosub BITLEN            ; A.x = bits
        ?nc gosub BPD               ; C.x = 3 (OCT) or 1 (BIN)
        c=c-1   x
        ? c#0   x
        ?nc go  PDY_MIN1            ; BIN: digits = bits
        a=a+c   x                   ; OCT: (bits + 2) div 3
        c=c+1   x
        ?nc gosub DIVQ
PDY_MIN1:
        ? a#0   x
        ?c go   PDY_ND
        a=a+1   x                   ; at least one digit
        ?nc go  PDY_ND
PDY_PAD:
        ?nc gosub SEL10
        c=reg 0
        a=0     w
        pt= 1
        a=c     wp                  ; A.x = W
        ?nc gosub BPD
        c=c-1   x
        ? c#0   x
        ?nc go  PDY_ND              ; BIN: W digits
        a=a+c   x                   ; (W + bpd - 1) div bpd
        c=c+1   x
        ?nc gosub DIVQ
PDY_ND:
        c=0     w
        sel ram
        c=reg 6
        rcr 8
        pt= 2
        c=0     p                   ; C.x = offset (RAM 6 digits 9-8)
        a=a-c   x                   ; A.x = n = digits from the offset
        ?c go   PDY_EMPTY           ; n < 0
        ? a#0   x
        ?nc go  PDY_EMPTY           ; n = 0
        c=0     w
        ldi $009
        ? a<c   x                   ; n <= 8 ?
        ?c go   PDY_S0
        s=1     13                  ; more digits to the left
        ?nc go  PDY_TY
PDY_S0:
        s=0     13
PDY_TY:
        ? s=1   7                   ; in entry: at least the digits typed
        ?nc go  PDY_N               ;  (typed leading zeros show), except
        c=0     w                   ;  with flag 3 in HEX, OCT, BIN
        sel ram
        c=reg 6
        pt= 13
        c=c+c   s
        ?c go   PDY_TY1             ; DEC
        pt= 6
        c=c+c   p                   ; flag 3 -> carry
        ?c go   PDY_N
PDY_TY1:
        ?nc gosub SEL10
        c=reg 0
        rcr 12                      ; C digits 1-0 = saved G
        pt= 2
        c=0     p
        c=c-1   x                   ; digits typed
        ? a<c   x
        ?nc go  PDY_N
        a=c     x
PDY_N:
        ac ex   x                   ; n -> CTRL digits 8-7 (scratch)
        rcr 7
        a=c     w
        ?nc gosub SEL10
        c=reg 0
        pt= 8
        ac ex   p
        pt= 7
        ac ex   p
        reg=c 0
        c=0     w
        sel ram
        c=reg 6
        pt= 13
        ? c#0   p
        ?nc go  PDY_HEX
        c=c+c   s
        ?c go   PDY_DEC
        ?nc gosub WIN_OB            ; OCT / BIN window
        ?nc go  PDY_CNT
PDY_HEX:
        ; HEX window: A digits 12-5 = nibbles offset .. offset+7
        c=0     w
        ldi $008
        pt= 0
        g=c                         ; G = 8 - j
PDY_WIN:
        c=0     w
        sel ram
        c=reg 6
        rcr 8
        pt= 2
        c=0     p
        a=c     x                   ; A.x = offset
        c=0     w
        ldi $008
        a=a+c   x
        c=0     w
        pt= 0
        c=g
        a=a-c   x                   ; A.x = k = offset + j
        ?nc gosub NIB               ; C digit 0 = nibble k
        a sr    m                   ; window right one digit (digits 12-3)
        rcr 2                       ; nibble to digit 12
        pt= 12
        a=c     p
        c=0     w
        pt= 0
        c=g
        c=c-1   x
        g=c
        ? c#0   x
        ?c go   PDY_WIN
        ?nc go  PDY_CNT
PDY_DEC:
        ; DEC window: A digits 12-5 = decimal digits offset .. offset+7
        c=0     w
        ldi $008
        pt= 0
        g=c                         ; G = 8 - j
PDY_DW:
        c=0     w
        sel ram
        c=reg 6
        rcr 8
        pt= 2
        c=0     p
        a=c     x                   ; A.x = offset
        c=0     w
        ldi $008
        a=a+c   x
        c=0     w
        pt= 0
        c=g
        a=a-c   x                   ; A.x = k = offset + j
        ?nc gosub DDIG              ; C digit 0 = decimal digit k
        a sr    m
        rcr 2
        pt= 12
        a=c     p
        c=0     w
        pt= 0
        c=g
        c=c-1   x
        g=c
        ? c#0   x
        ?c go   PDY_DW
        c=n
        m=c                         ; M = X again
PDY_CNT:
        ; count = n + 1 -> G and C low digits
        ?nc gosub SEL10
        c=reg 0
        rcr 7                       ; C digits 1-0 = n
        pt= 2
        c=0     p
        c=c+1   x
        pt= 0
        g=c                         ; G = n + 1
        a=c     x                   ; A.x = n + 1
        c=0     w
        sel ram
        c=reg 6
        ac ex   x                   ; C = RAM 6 with count in digits 2-0
        bc ex   w                   ; B = that
        c=0     w
        c=c-1   s
        pt= 13
        a=c     p                   ; A digit 13 = f
        c=b     w
        pt= 4
        a=0     wp                  ; A digits 4-0 = 0
        ?nc go  PDY_RENDER
PDY_EMPTY:
        s=0     13
        c=0     w
        sel ram
        c=reg 6
        pt= 13
        c=c+c   s                   ; DEC: M = X again (was the BCD)
        ?nc go  PDY_E1
        c=n
        m=c
PDY_E1:
        c=0     w                   ; nothing at this offset: the inputs the
        c=c+1   w                   ;  original uses for an empty window:
        pt= 0                       ;  G = 1, A = f000.., C = 1 at digit 7,
        g=c                         ;  base at 5, complement at 4
        c=0     w
        sel ram
        c=reg 6
        rcr 8
        a=0     w
        pt= 5
        ac ex   p
        pt= 4
        ac ex   p
        ac ex   w
        pt= 7
        lc 1
        bc ex   w
        c=0     w
        c=c-1   s
        a=c     w
        c=b     w
        ?nc go  PDY_RENDER
PDY_RENDER:
        pt= 12
        rtn
PDY_ORIG:
        c=0     w
        sel ram
        s=0     13                  ; displaced: s=0 13 ; set hex
        set hex
        ?nc golong FORMAT_CONT

; FULL_NDIG: significant hex digits of the full X (at least 1) in A.x.
; Uses A.x, C (no calls).
FULL_NDIG:
        c=0     w
        ldi $010
        sel ram
        c=reg 6                     ; X bits 120-127 -> digits 13-12
        rcr 2
        pt= 11
        c=0     wp
        a=0     x
FN_1:
        ? c#0   w
        ?nc go  FN_1E
        c sr    w
        a=a+1   x
        ?nc go  FN_1
FN_1E:
        ? a#0   x
        ?nc go DN_E
        c=0     w
        ldi $012                    ; count 13..14 -> 31..32 digits
        a=a+c   x
        rtn
DN_E:
        c=reg 1                     ; X bits 64-119
        a=0     x
FN_2:
        ? c#0   w
        ?nc go  FN_2E
        c sr    w
        a=a+1   x
        ?nc go  FN_2
FN_2E:
        ? a#0   x
        ?nc go DN_7
        c=0     w
        ldi $010                    ; + 16
        a=a+c   x
        rtn
DN_7:
        c=0     w
        sel ram
        c=reg 7                     ; X bits 56-63 -> digits 13-12
        rcr 2
        pt= 11
        c=0     wp
        a=0     x
FN_3:
        ? c#0   w
        ?nc go  FN_3E
        c sr    w
        a=a+1   x
        ?nc go  FN_3
FN_3E:
        ? a#0   x
        ?nc go DN_N
        c=0     w
        ldi $002                    ; count 13..14 -> 15..16 digits
        a=a+c   x
        rtn
DN_N:
        c=n                         ; X bits 0-55
        a=0     x
FN_4:
        ? c#0   w
        ?nc go  FN_4E
        c sr    w
        a=a+1   x
        ?nc go  FN_4
FN_4E:
        ? a#0   x
        ?c rtn
        a=a+1   x                   ; X = 0: one digit
        rtn
; DIVQ: A.x = A.x div C.x, C.x = the remainder (C.x > 0). Uses A, C.
DIVQ:
        a=0     m
DQ_L:
        ? a<c   x
        ?c go   DQ_D
        a=a-c   x
        a=a+1   m
        ?nc go  DQ_L
DQ_D:
        ac ex   x                   ; C.x = remainder
        a sr    w
        a sr    w
        a sr    w                   ; A.x = quotient
        rtn

; BPD: C.x = bits per digit of the current base: 4 HEX, 3 OCT, 1 BIN
; (other digits of C 0). Uses C; RAM select 0x000.
BPD:
        c=0     w
        sel ram
        c=reg 6
        pt= 13
        ? c#0   p
        ?c go   BP_OB
        c=0     w
        ldi $004
        rtn
BP_OB:
        c=c+c   s
        c=c+c   s
        ?c go   BP_B
        c=0     w
        ldi $003
        rtn
BP_B:
        c=0     w
        ldi $001
        rtn

; BITLEN: A.x = bit length of the full X (0 for X = 0). Uses A.x, C, G.
BITLEN:
        ?nc gosub FULL_NDIG         ; A.x = nd (at least 1)
        ac ex   x
        pt= 0
        g=c                         ; G = nd
        a=c     x
        a=a-1   x
        ?nc gosub NIB               ; C digit 0 = top digit t
        a=0     x
        pt= 0
        a=c     p                   ; A.x = t
        c=0     w
        ldi $008
        ? a<c   x
        ?nc go  BL_4
        ldi $004
        ? a<c   x
        ?nc go  BL_3
        ldi $002
        ? a<c   x
        ?nc go  BL_2
        c=0     w
        ? a#0   x
        ?nc go  BL_D
        c=c+1   x                   ; t = 1
        ?nc go  BL_D
BL_4:
        c=0     w
        ldi $004
        ?nc go  BL_D
BL_3:
        c=0     w
        ldi $003
        ?nc go  BL_D
BL_2:
        c=0     w
        ldi $002
BL_D:
        ac ex   x                   ; A.x = bit length of t
        c=0     w
        pt= 0
        c=g
        c=c-1   x
        c=c+c   x
        c=c+c   x
        a=a+c   x                   ; + 4 (nd - 1)
        rtn

; SHL1X: X = 2 X over all 128 bits. SHL1XC: X = 2 X + A.x (0 or 1).
; Out: A.x = old bit 127. Uses A.x, C, G. RAM select ends at 0x000.
SHL1X:
        a=0     x
SHL1XC:
        set hex
        c=n
        c=c+c   w                   ; bits 0-55
        ?c go   SH_C1
        c=a+c   x                   ; + bit in (bit 0 is 0 here)
        a=0     x
        ?nc go  SH_N0
SH_C1:
        c=a+c   x
        a=0     x
        a=a+1   x                   ; carry into bit 56
SH_N0:
        n=c
        m=c
        c=0     w
        sel ram
        c=reg 7
        ?nc gosub DBLB              ; bits 56-63 (RAM 7 digits 1-0)
        c=reg 7
        pt= 0
        c=g
        reg=c 7
        ?nc gosub SEL10
        c=reg 1
        c=c+c   w                   ; bits 64-119
        ?nc go  SH_E0
        c=a+c   x
        a=0     x
        a=a+1   x
        ?nc go  SH_E1
SH_E0:
        c=a+c   x
        a=0     x
SH_E1:
        reg=c 1
        c=reg 6
        ?nc gosub DBLB              ; bits 120-127 (reg 6 digits 1-0)
        c=reg 6
        pt= 0
        c=g
        reg=c 6
        c=0     w
        sel ram
        rtn

; DBLB: C digits 1-0 = a byte, A.x = carry in (0/1). Out: G = 2 byte +
; carry in (low 8 bits), A.x = carry out. Uses C.
DBLB:
        pt= 0
        g=c
        c=0     w
        c=g
        c=c+c   x
        c=a+c   x
        a=0     x
        pt= 2
        ? c#0   p
        ?nc go  DB_1
        a=a+1   x
DB_1:
        pt= 0
        g=c
        rtn

; SHR4X: X = X / 16 over all 128 bits, bits 124-127 = B digit 1. Uses A, C, G. RAM select 0x000.
SHR4X:
        c=n
        c sr    w
        a=c     w                   ; A = N / 16
        c=0     w
        sel ram
        c=reg 7
        rcr 1                       ; C digit 13 = X bits 56-59
        pt= 13
        ac ex   p
        ac ex   w
        n=c                         ; new X bits 0-55
        m=c
        c=reg 7
        pt= 0
        g=c                         ; G = X bits 56-63
        c=0     w
        c=g
        c sr    x                   ; byte / 16
        g=c
        ?nc gosub SEL10
        c=reg 1
        rcr 13                      ; C digit 1 = extension digit 0
        a=c     w
        c=0     w
        pt= 0
        c=g
        pt= 1
        ac ex   p                   ; byte digit 1 = extension digit 0
        pt= 0
        g=c
        c=0     w
        sel ram
        c=reg 7
        pt= 0
        c=g
        reg=c 7                     ; new X bits 56-63
        ?nc gosub SEL10
        c=reg 1
        c sr    w
        a=c     w
        c=reg 6
        rcr 1                       ; C digit 13 = X bits 120-123
        pt= 13
        ac ex   p
        ac ex   w
        reg=c 1                     ; new X bits 64-119
        c=reg 6
        pt= 0
        g=c
        c=0     w
        c=g
        c sr    x
        pt= 1
        c=b     p                   ; bits 124-127 from B digit 1
        pt= 0
        g=c
        c=reg 6
        c=g
        reg=c 6                     ; new X bits 120-127
        c=0     w
        sel ram
        rtn

; WIN_OB: OCT/BIN window: A digits 12-5 = the base-8 or base-2 digits
; o .. o+7 of the full X (o = RAM 6 digits 9-8), most significant at
; digit 12. Fetches the nibbles under the window into the top of a
; register, aligns the window's top bit with bit 55 and takes the bits
; off the top. Uses A, B, C, G.
WIN_OB:
        c=0     w
        sel ram
        c=reg 6
        rcr 8
        pt= 2
        c=0     p
        a=0     w
        a=c     x                   ; A.x = o
        ?nc gosub BPD
        c=c-1   x
        ? c#0   x
        ?nc go  WO_B0               ; BIN: first bit = o
        ac ex   x                   ; OCT: first bit = 3 o
        a=c     x
        a=a+c   x
        a=a+c   x
WO_B0:
        c=0     w
        ldi $004
        ?nc gosub DIVQ              ; A.x = q (nibble), C.x = s (bit)
        ac ex   x                   ; C.x = q, A.x = s
        pt= 0
        g=c                         ; G = q
        c=0     w
        ldi $004
        ac ex   x                   ; A.x = 4, C.x = s
        c=a-c   x                   ; C.x = 4 - s (shift, 1..4)
        rcr 1
        a=0     w
        pt= 13
        a=c     p                   ; A digit 13 = 4 - s
        ?nc gosub BPD
        c=c+c   x
        c=c+1   x                   ; nibbles to fetch: 3 (BIN), 7 (OCT)
        rcr 2
        pt= 12
        a=c     p                   ; A digit 12 = count
        b=0     w                   ; B = fetched nibbles, at the top
WO_L:
        c=0     w
        pt= 0
        c=g
        a=c     x                   ; A.x = k
        ?nc gosub NIB               ; C digit 0 = nibble k
        rcr 1                       ; to digit 13
        bc ex   w
        c sr    w
        pt= 13
        c=b     p
        bc ex   w                   ; B = B / 16 + nibble at the top
        c=0     w
        pt= 0
        c=g
        c=c+1   x
        g=c                         ; k + 1
        pt= 12
        a=a-1   p
        ? a#0   p
        ?c go   WO_L
        c=b     w
WO_S:
        c=c+c   w                   ; window top bit to bit 55
        pt= 13
        a=a-1   p
        ? a#0   p
        ?c go   WO_S
        bc ex   w                   ; B = aligned bits
        c=0     w
        pt= 13
        lc 8
        a=c     w                   ; A = 0, digit 13 = 8 (digit counter)
        ?nc gosub BPD
        c=c-1   x
        ? c#0   x
        ?c go   WO_OCT
        bc ex   w                   ; C = bits
WO_BL:
        a sl    m                   ; BIN: one bit per digit
        c=c+c   w
        ?nc go  WO_B1
        pt= 5
        a=a+1   p
WO_B1:
        pt= 13
        a=a-1   p
        ? a#0   p
        ?c go   WO_BL
        rtn
WO_OCT:
        bc ex   w                   ; C = bits
WO_OL:
        a sl    m                   ; OCT: three bits per digit
        pt= 5
        c=c+c   w
        ?nc go  WO_O1
        a=a+1   p
        a=a+1   p
        a=a+1   p
        a=a+1   p
WO_O1:
        c=c+c   w
        ?nc go  WO_O2
        a=a+1   p
        a=a+1   p
WO_O2:
        c=c+c   w
        ?nc go  WO_O3
        a=a+1   p
WO_O3:
        pt= 13
        a=a-1   p
        ? a#0   p
        ?c go   WO_OL
        rtn

; SIGNX: C.x = 1 if X is negative (1's or 2's complement and bit W - 1
; set), else C = 0. Uses A, B, C, G. RAM select ends 0x000 or 0x010.
SIGNX:
        c=0     w
        sel ram
        c=reg 6
        pt= 12
        ? c#0   p                   ; unsigned: never negative
        ?c go   SX_S
        c=0     w
        rtn
SX_S:
        ?nc gosub SEL10
        c=reg 0
        a=0     w
        pt= 1
        a=c     wp                  ; A.x = W
        a=a-1   x                   ; sign bit W - 1
        c=0     w
        ldi $004
        ?nc gosub DIVQ              ; A.x = its nibble, C.x = bit in it
        pt= 0
        g=c
        ?nc gosub NIB               ; C digit 0 = the nibble
        a=0     w
        pt= 0
        a=c     p                   ; A.x = nibble
        c=0     w
        c=g                         ; C.x = bit s (0-3)
        ? c#0   x
        ?nc go  SX_0
        c=c-1   x
        ? c#0   x
        ?nc go  SX_1
        c=c-1   x
        ? c#0   x
        ?nc go  SX_2
        c=0     w                   ; s = 3
        ldi $010
        bc ex   w
        c=0     w
        ldi $008
        ?nc go  SX_T
SX_0:
        c=0     w
        ldi $002
        bc ex   w
        c=0     w
        ldi $001
        ?nc go  SX_T
SX_1:
        c=0     w
        ldi $004
        bc ex   w
        c=0     w
        ldi $002
        ?nc go  SX_T
SX_2:
        c=0     w
        ldi $008
        bc ex   w
        c=0     w
        ldi $004
SX_T:
        ? a<b   x                   ; nibble mod 2^(s+1) (B) ...
        ?c go   SX_C
        a=a-b   x
        ?nc go  SX_T
SX_C:
        ? a<c   x                   ; ... >= 2^s (C): bit s set
        ?c go   SX_NO
        c=0     w
        c=c+1   x
        rtn
SX_NO:
        c=0     w
        rtn

; DECBCD: binary to decimal of the magnitude: X bits W-1..0, inverted if
; s3 (1's complement magnitude), + 1 if s3 in 2's complement. Out: A =
; BCD digits 0-13, B = digits 14-27, RAM 4 = digits 28-41 (128 bits need
; 39). Double dabble over four chunks (X bits 120-127, 64-119, 56-63,
; 0-55). RAM 4 is the renderer's copy of A, rewritten by every render,
; so free here. Uses C, M, G. RAM select 0x000, hex mode.
DECBCD:
        set hex
        c=0     w
        sel ram
        reg=c 4                     ; digits 28-41 = 0
        ldi $010
        sel ram
        c=reg 0
        a=0     w
        pt= 1
        a=c     wp                  ; A.x = W
        c=0     w
        ldi $040
        a=a-c   x                   ; k = W - 64 (1-64)
        c=0     w
        ldi $038
        ? a<c   x
        ?c go   DBC_K                ; k < 56: no bits 120-127
        a=a-c   x                   ; bits 120-127 in use: k - 56 (0-8)
        ?nc gosub ONES
        a=c     w                   ; A = mask for the byte
        c=0     w
        ldi $008
        pt= 0
        g=c
        ldi $010
        sel ram
        c=reg 6
        ? s=1   3
        ?nc go  DBC_1
        c=-c-1  w
DBC_1:
        c=c&a                       ; X bits 120-127 (digits 1-0)
        rcr 2                       ; to the top
        m=c
        a=0     w
        b=0     w
        c=0     w
        sel ram
        c=m
        ?nc gosub DD_CHUNK
        c=0     w                   ; bits 64-119, all within W
        ldi $038
        pt= 0
        g=c
        ldi $010
        sel ram
        c=reg 1
        ? s=1   3
        ?nc go  DBC_2
        c=-c-1  w
DBC_2:
        m=c
        c=0     w
        sel ram
        c=m
        ?nc gosub DD_CHUNK
        ?nc go  DBC_3
DBC_K:
        ?nc gosub ONES              ; mask 2^k - 1 for bits 64-119
        a=c     w
        c=0     w
        ldi $038
        pt= 0
        g=c
        ldi $010
        sel ram
        c=reg 1
        ? s=1   3
        ?nc go  DBC_K1
        c=-c-1  w
DBC_K1:
        c=c&a
        m=c
        a=0     w
        b=0     w
        c=0     w
        sel ram
        c=m
        ?nc gosub DD_CHUNK
DBC_3:
        c=0     w                   ; bits 56-63: RAM 7 digits 1-0
        ldi $008
        pt= 0
        g=c
        c=0     w
        sel ram
        c=reg 7
        ? s=1   3
        ?nc go  DBC_31
        c=-c-1  w
DBC_31:
        rcr 2
        pt= 11
        c=0     wp
        ?nc gosub DD_CHUNK
        c=0     w                   ; bits 0-55: N
        ldi $038
        pt= 0
        g=c
        c=n
        ? s=1   3
        ?nc go  DBC_41
        c=-c-1  w                   ; (sets carry)
DBC_41:
        pt= 0
        ?nc gosub DD_CHUNK
        ? s=1   3
        ?nc rtn
        c=0     w
        sel ram
        c=reg 6
        pt= 12
        c=c-1   p
        ? c#0   p                   ; 2's complement: + 1
        ?nc rtn
        set dec
        a=a+1   w
        ?nc go  DBC_X
        c=b     w
        c=c+1   w
        ?c go   DBC_Z
        bc ex   w
        ?nc go  DBC_X
DBC_Z:
        bc ex   w
        c=reg 4
        c=c+1   w
        reg=c 4
DBC_X:
        set hex
        rtn

; DD_CHUNK: double dabble one chunk: C = bits, top-aligned (from digit
; 13 down), G = number of bits. BCD in A (digits 0-13), B (14-27), RAM 4
; (28-41); RAM select 0x000. While the BCD is 0, leading zero nibbles are
; skipped. Uses C, M, G. Hex mode in and out.
DD_CHUNK:
        m=c
        ? a#0   w
        ?c go   DC_BIT
        ? b#0   w
        ?c go   DC_BIT
        c=reg 4
        ? c#0   w
        ?c go   DC_BIT
        c=m
        ? c#0   w
        ?nc rtn                     ; BCD 0, chunk 0: nothing to do
DC_Z:
        pt= 13
        ? c#0   p
        ?c go   DC_BIT
        rcr 13                      ; skip a zero nibble
        m=c
        c=0     w
        pt= 0
        c=g
        c=c-1   x
        c=c-1   x
        c=c-1   x
        c=c-1   x
        g=c
        c=m
        ?nc go  DC_Z
DC_BIT:
        set dec
        c=reg 4
        c=c+c   w                   ; digits 28-41 * 2
        reg=c 4
        c=b     w
        c=c+c   w                   ; digits 14-27 * 2
        ?nc go  DC_1
        bc ex   w
        c=reg 4
        c=c+1   w                   ; carry into digit 28
        reg=c 4
        ?nc go  DC_2
DC_1:
        bc ex   w
DC_2:
        c=0     w
        c=a+c   w
        a=a+c   w                   ; digits 0-13 * 2
        ?nc go  DC_3
        c=b     w
        c=c+1   w                   ; carry into digit 14
        bc ex   w
DC_3:
        set hex
        c=m
        c=c+c   w                   ; next bit -> carry
        ?nc go  DC_4
        a=a+1   w                   ; + the bit (digit 0 is even)
DC_4:
        m=c
        c=0     w
        pt= 0
        c=g
        c=c-1   x
        g=c
        ? c#0   x
        ?c go   DC_BIT
        rtn

; ONES: C = 2^A.x - 1 (A.x <= 56), A.x = 0. Hex mode.
ONES:
        c=0     w
ON_L:
        ? a#0   x
        ?nc rtn
        c=c+c   w
        c=c+1   w
        a=a-1   x
        ?nc go  ON_L

; DEC_ND: A.x = decimal digits of the BCD in M (digits 0-13), B (14-27),
; RAM 4 (28-41), at least 1. Uses C. RAM select 0x000.
DEC_ND:
        c=0     w
        sel ram
        c=reg 4
        ?nc gosub HIGHDIG
        ? a#0   x
        ?nc go  DE_B
        c=0     w
        ldi $01c                    ; + 28
        a=a+c   x
        rtn
DE_B:
        c=b     w
        ?nc gosub HIGHDIG
        ? a#0   x
        ?nc go  DE_A
        c=0     w
        ldi $00e                    ; + 14
        a=a+c   x
        rtn
DE_A:
        c=m
        ?nc gosub HIGHDIG
        ? a#0   x
        ?c rtn
        a=a+1   x                   ; 0: one digit
        rtn

; DDIG: A.x = k -> C digit 0 = decimal digit k of the BCD in M, B, RAM 4.
; Uses A.x, C; A digits 12-3 kept. RAM select 0x000.
DDIG:
        c=0     w
        ldi $00e
        ? a<c   x
        ?c go   DG_L
        a=a-c   x
        ? a<c   x
        ?c go   DG_M
        a=a-c   x
        c=0     w
        sel ram
        c=reg 4
        ?nc go  DG_R
DG_L:
        c=m
        ?nc go  DG_R
DG_M:
        c=b     w
DG_R:
        ? a#0   x
        ?nc rtn
        rcr 1
        a=a-1   x
        ?nc go  DG_R

; MUL10X: X = 10 X + d over 128 bits. In: A = d (0-9, other digits 0).
; Out: A.x = carry beyond bit 127 (0-9), M = N. Uses B, C. RAM select
; 0x000, hex mode.
MUL10X:
        set hex
        c=n
        ?nc gosub MUL10W            ; bits 0-55
        n=c
        c=0     w
        sel ram
        c=reg 7
        ?nc gosub M10B              ; bits 56-63
        reg=c 7
        ?nc gosub SEL10
        c=reg 1
        ?nc gosub MUL10W            ; bits 64-119
        reg=c 1
        c=reg 6
        ?nc gosub M10B              ; bits 120-127
        reg=c 6
        c=0     w
        sel ram
        c=n
        m=c
        rtn

; MUL10W: C = 56-bit word, A = carry in (0-9, rest 0). Out: C = (10 C +
; A) mod 2^56, A = carry out (0-9). Two 28-bit halves. Uses B, M.
MUL10W:
        m=c                         ; M = word
        b=a     w                   ; B = carry in
        a=0     w
        pt= 6
        a=c     wp                  ; A = low half
        ac ex   w                   ; C = low half, A = word
        ab ex   w                   ; A = carry in, B = word
        ?nc gosub M10H              ; C = low', A = carry
        bc ex   w                   ; B = low', C = word
        rcr 7                       ; C digits 6-0 = high half
        bc ex   w                   ; B = rotated word, C = low'
        m=c                         ; M = low'
        c=0     w
        pt= 6
        c=b     wp                  ; C = high half
        ?nc gosub M10H              ; C = high', A = carry out
        rcr 7                       ; high' to digits 13-7
        bc ex   w                   ; B = high' << 28
        c=m                         ; C = low'
        ab ex   w                   ; A = high' << 28, B = carry out
        c=a+c   w
        ab ex   w                   ; A = carry out
        rtn

; M10B: C = a register with a byte at digits 1-0, A = carry in (0-9).
; Out: C = the register with byte (10 byte + A) mod 256, A = carry out.
; Uses B.
M10B:
        bc ex   w                   ; B = register
        c=0     w
        pt= 1
        c=b     wp                  ; C = byte
        ?nc gosub M10H              ; C = 10 byte + carry (< 0xa00), A = 0
        a=0     w
        pt= 2
        a=c     p                   ; A = carry out << 8
        c=0     p
        a sr    w
        a sr    w                   ; A = carry out
        bc ex   w                   ; B = new byte, C = register
        pt= 1
        c=b     wp
        rtn

; M10H: C = q (digits 13-7 zero), A = carry in (0-9, rest 0). Out: C =
; (10 q + A) mod 16^7, A = (10 q + A) div 16^7. Uses A, C. Hex mode.
M10H:
        ac ex   w                   ; A = q, C = carry in
        rcr 1                       ; carry in to digit 13
        a=a+c   w                   ; A = q + carry in << 52
        c=0     w
        c=a+c   w
        pt= 13
        c=0     p                   ; C = q
        c=c+c   w
        c=c+c   w                   ; 4 q
        c=a+c   w                   ; 5 q + carry in << 52
        a=0     w
        a=c     p                   ; A = carry in << 52
        c=0     p                   ; C = 5 q
        c=c+c   w                   ; 10 q
        ac ex   w
        rcr 13                      ; C = carry in, A = 10 q
        c=a+c   w                   ; 10 q + carry in (< 16^8)
        a=0     w
        pt= 7
        a=c     p                   ; A = carry out << 28
        c=0     p
        ac ex   w
        rcr 7
        ac ex   w                   ; A = carry out, C = low 7 digits
        rtn

; DIV10X: X = (A 2^128 + X) div 10 over 128 bits. In: A = remainder in
; (0-9, other digits 0). Out: A.x = remainder, M = N. Uses B, C. RAM
; select 0x000, hex mode.
DIV10X:
        set hex
        ?nc gosub SEL10
        c=reg 6
        ?nc gosub D10B              ; bits 120-127
        reg=c 6
        c=reg 1
        ?nc gosub D10W              ; bits 64-119
        reg=c 1
        c=0     w
        sel ram
        c=reg 7
        ?nc gosub D10B              ; bits 56-63
        reg=c 7
        c=n
        ?nc gosub D10W              ; bits 0-55
        n=c
        m=c
        rtn

; D10B: C = a register with a byte at digits 1-0, A = remainder in (0-9).
; Out: C = the register with byte (A 256 + byte) div 10, A = remainder.
; Uses B, M.
D10B:
        m=c                         ; M = register
        rcr 2
        pt= 11
        c=0     wp
        rcr 12                      ; C = byte
        ac ex   w                   ; A = byte, C = remainder
        rcr 12                      ; C = remainder << 8
        a=a+c   w
        ?nc gosub DIV10V            ; C = quotient, A = remainder
        bc ex   w                   ; B = quotient
        c=m
        pt= 1
        c=b     wp
        rtn

; D10W: C = 56-bit word, A = remainder in (0-9, rest 0). Out: C = (A 2^56
; + C) div 10, A = remainder. Two 28-bit halves. Uses B, M.
D10W:
        m=c                         ; M = word
        rcr 7                       ; C digits 6-0 = high half
        ac ex   w                   ; A = rotated word, C = remainder
        rcr 7                       ; C = remainder << 28
        ac ex   w
        pt= 6
        a=c     wp                  ; A = remainder << 28 + high half
        ?nc gosub DIV10V            ; C = q high, A = remainder
        bc ex   w                   ; B = q high
        c=m
        ac ex   w                   ; A = word, C = remainder
        rcr 7
        ac ex   w
        pt= 6
        a=c     wp                  ; A = remainder << 28 + low half
        c=b     w
        m=c                         ; M = q high
        ?nc gosub DIV10V            ; C = q low, A = remainder
        bc ex   w                   ; B = q low
        c=m
        rcr 7                       ; q high << 28
        ac ex   w                   ; A = q high << 28, C = remainder
        a=a+b   w
        ac ex   w                   ; C = quotient, A = remainder
        rtn

; DIV10V: A = v (< 10 16^7). Out: C = v div 10, A = v mod 10. One hex
; digit of the quotient per pass (repeated subtraction). Uses B.
DIV10V:
        c=0     w
        pt= 6
        lc a                        ; C = 10 16^6
        bc ex   w
        c=0     w
DV_J:
        ? a<b   w
        ?c go   DV_N
        a=a-b   w
        c=c+1   w
        ?nc go  DV_J
DV_N:
        b sr    w
        ? b#0   w
        ?nc rtn
        rcr 13                      ; quotient one digit up
        ?nc go  DV_J

; HIGHDIG: A.x = number of significant digits of C (0 if C = 0).
; Destroys C; uses A.x only.
HIGHDIG:
        a=0     x
HD_LOOP:
        ? c#0   w
        ?nc rtn
        c sr    w
        a=a+1   x
        ?nc go HD_LOOP

; NIB: A.x = k (0..) -> C digit 0 = hex digit k of the full X (0 for
; k >= 32). Uses A.x (left 0), C; A digits 12-3 kept. RAM select ends at
; 0x000 or 0x010.
NIB:
        c=0     w
        ldi $00e
        ? a<c   x
        ?c go NB_N                ; 0..13: N
        a=a-c   x
        c=0     w
        ldi $002
        ? a<c   x
        ?c go NB_7                ; 14..15: RAM 7 digits 1-0
        a=a-c   x
        c=0     w
        ldi $00e
        ? a<c   x
        ?c go NB_E                ; 16..29: extension reg 1
        a=a-c   x
        c=0     w
        ldi $002
        ? a<c   x
        ?c go NB_H                ; 30..31: reg 6 digits 1-0
        c=0     w
        rtn
NB_N:
        c=n
        ?nc go NB_R
NB_7:
        c=0     w
        sel ram
        c=reg 7
        ?nc go NB_R
NB_E:
        c=0     w
        ldi $010
        sel ram
        c=reg 1
        ?nc go NB_R
NB_H:
        c=0     w
        ldi $010
        sel ram
        c=reg 6
NB_R:
        ? a#0   x
        ?nc rtn
        rcr 1
        a=a-1   x
        ?nc go NB_R

; P_DIGIT: digit key (hook 0x0C84). In: A.x = digit, s7 = entry active,
; s9 = stack lift disabled, s12 = float mode. Plus mode, HEX/OCT/BIN:
; - new number: lift the extension if the original lifts (s9 = 0) and
;   clear X's extension, then the original entry;
; - while X * base + digit fits in 64 bits: the original entry;
; - beyond: X = X * base + digit over all 128 bits if bits(X) + bits per
;   digit <= W, else the key is ignored (the original's rule).
; Uses C, G, M, N; A.x is kept for the original path. B is not touched.
P_DIGIT:
        ? s=1   12
        ?c golong DIGIT_FLT         ; float entry: original
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        ? c#0   p
        ?nc go  PDG_ORIG
        c=0     w
        sel ram
        c=reg 6
        pt= 13
        ? c#0   p
        ?nc go  PDG_VALID           ; HEX: every digit
        c=c+c   s
        ?c go   PDG_R10             ; DEC
        c=c+c   s
        ?c go   PDG_R2              ; BIN
        ldi $008                    ; OCT
        ?nc go  PDG_RCMP
PDG_R2:
        ldi $002
        ?nc go  PDG_RCMP
PDG_R10:
        ldi $00a
PDG_RCMP:
        ? a<c   x                   ; not a digit of this base: the
        ?nc go  PDG_ORIG            ;  original ignores it
PDG_VALID:
        ? s=1   7                   ; entry active?
        ?c go   PDG_MORE
        ?nc gosub SEL10             ; new number
        ? s=1   9
        ?c go   PDG_NOLIFT
        ?nc gosub EXT_LIFT
PDG_NOLIFT:
        c=0     w
        reg=c 1                     ; X bits 64-119 = 0
        c=reg 6
        pt= 1
        c=0     wp
        reg=c 6                     ; X bits 120-127 = 0
PDG_ORIG:
        c=0     w
        sel ram
        ?nc golong DIGIT_INT
PDG_MORE:
        ?nc gosub SEL10             ; save digit and count in CTRL scratch:
        c=reg 0
        rcr 3
        pt= 0
        ac ex   p                   ; C digit 0 = A digit 0 (the digit)
        rcr 1                       ; digits 5-4 -> 1-0
        pt= 0
        c=g
        rcr 10                      ; back in place
        reg=c 0
        c=0     w
        sel ram
        c=reg 6
        pt= 13
        c=c+c   s
        ?c go   PDG_DEC
        ?nc gosub BITLEN            ; A.x = bits of X
        ?nc gosub BPD
        a=a+c   x                   ; bits after this digit (at most)
        c=0     w
        ldi $041
        ? a<c   x                   ; <= 64: the original handles it
        ?c go   PDG_BACK
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        c=0     p                   ; C.x = W
        c=c+1   x
        ? a<c   x                   ; <= W ?
        ?nc go  PDG_IGNORE
        ?nc gosub BPD               ; X = X << bits per digit
        c=c-1   x
        ? c#0   x
        ?nc go  PDG_S1              ; BIN: 1
        c=c-1   x
        c=c-1   x
        ? c#0   x
        ?nc go  PDG_S3              ; OCT: 3
        ?nc gosub SHL1X             ; HEX: 4
PDG_S3:
        ?nc gosub SHL1X
        ?nc gosub SHL1X
PDG_S1:
        ?nc gosub SHL1X
        ?nc gosub SEL10             ; + digit (low bits are zero)
        c=reg 0
        rcr 3
        a=0     w
        pt= 0
        a=c     p                   ; A = digit
        c=0     w
        sel ram
        c=n
        c=a+c   w
        n=c
        m=c
PDG_INC:
        ?nc gosub SEL10             ; G = count + 1
        c=reg 0
        rcr 4
        c=c+1   x
        pt= 0
        g=c
        c=0     w
        sel ram
        ?nc golong MAIN_DISP
PDG_IGNORE:
        c=0     w                   ; digit does not fit: key ignored
        ldi $010
        sel ram
        c=reg 0
        rcr 4
        pt= 0
        g=c                         ; G = count (unchanged)
        c=0     w
        sel ram
        ?nc golong MAIN_DISP
PDG_BACK:
        ?nc gosub SEL10             ; restore G and A.x = digit, original
        c=reg 0
        rcr 4
        pt= 0
        g=c
        c=reg 0
        rcr 3
        a=0     x
        pt= 0
        a=c     p
        ?nc go  PDG_ORIG

PDG_DEC:
        ; DEC: X = 10 X + d over 128 bits, undone if beyond W bits
        ?nc gosub SEL10
        c=reg 0
        rcr 3
        a=0     w
        pt= 0
        a=c     p                   ; A = digit
        ?nc gosub MUL10X            ; A.x = carry beyond 128 bits
        ? a#0   x
        ?c go   PDG_UNDO
        ?nc gosub BITLEN
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        c=0     p
        c=c+1   x                   ; C.x = W + 1
        ? a<c   x                   ; bits <= W ?
        ?c go   PDG_INC
        a=0     w
PDG_UNDO:
        ?nc gosub DIV10X            ; X = (carry 2^128 + X) div 10
        ?nc go  PDG_IGNORE

; P_BSP: backspace (hook 0x1400). Outside entry (s7 = 0) the original
; clears X; in plus mode X's extension is cleared too. In entry, plus
; mode, HEX/OCT/BIN, X wider than 64 bits: X = X >> bits per digit over
; all 128 bits (as (X / 16) << (4 - bpd) + low nibble >> bpd), count - 1.
; Otherwise the original.
P_BSP:
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        ? c#0   p
        ?nc go  PB_ORIG
        ? s=1   7
        ?c go   PB_ENTRY
        c=0     w                   ; not in entry: X = 0 incl. extension
        reg=c 1
        c=reg 6
        pt= 1
        c=0     wp
        reg=c 6
        c=0     w
        sel ram
        ?nc golong BSP_CLX
PB_ENTRY:
        c=0     w
        sel ram
        c=reg 6
        pt= 13
        c=c+c   s                   ; DEC (float does not get here)
        ?c go   PB_DEC
        ?nc gosub SEL10             ; save G in CTRL digits 5-4
        c=reg 0
        rcr 4
        pt= 0
        c=g
        rcr 10
        reg=c 0
        ?nc gosub BITLEN
        c=0     w
        ldi $041
        ? a<c   x                   ; X fits 64 bits: the original
        ?c go   PB_ORIG_G
        c=n                         ; low nibble -> CTRL digit 3
        a=c     w
        ?nc gosub SEL10
        c=reg 0
        rcr 3
        pt= 0
        ac ex   p
        rcr 11
        reg=c 0
        b=0     w
        ?nc gosub SHR4X             ; X / 16
        c=0     w
        sel ram
        c=reg 6
        pt= 13
        ? c#0   p
        ?nc go  PB_DONE             ; HEX: done
        c=c+c   s
        c=c+c   s
        ?c go   PB_BIN
        ?nc gosub SHL1X             ; OCT: << 1, + nibble >> 3
        c=0     w
        ldi $008
        ?nc go  PB_ADD
PB_BIN:
        ?nc gosub SHL1X             ; BIN: << 3, + nibble >> 1
        ?nc gosub SHL1X
        ?nc gosub SHL1X
        c=0     w
        ldi $002
PB_ADD:
        bc ex   w                   ; B = divisor
        ?nc gosub SEL10
        c=reg 0
        rcr 3
        a=0     w
        pt= 0
        a=c     p                   ; A.x = low nibble
        c=b     w
        ?nc gosub DIVQ              ; A.x = nibble >> bpd
        c=n
        c=a+c   w
        n=c
        m=c
PB_DONE:
        ?nc gosub SEL10             ; G = saved count - 1
        c=reg 0
        rcr 4
        c=c-1   x
        pt= 0
        g=c
        c=0     w
        sel ram
        ?nc golong MAIN_DISP
PB_DEC:
        c=0     w                   ; DEC: X = X div 10 while 2 or more
        pt= 0                       ;  digits are left, else the original
        c=g
        a=c     w                   ; A.x = G = digits + 1
        c=0     w
        ldi $003
        ? a<c   x
        ?c go   PB_ORIG
        a=0     w
        ?nc gosub DIV10X
        c=0     w
        pt= 0
        c=g
        c=c-1   x
        g=c
        c=0     w
        sel ram
        ?nc golong MAIN_DISP
PB_ORIG_G:
        ?nc gosub SEL10             ; restore G
        c=reg 0
        rcr 4
        pt= 0
        g=c
PB_ORIG:
        c=0     w
        sel ram
        ? s=1   7                   ; displaced: ? s=1 7 ; ?nc golong $0c40
        ?nc golong BSP_CLX
        ?nc golong BSP_ENTRY

; P_MEMRESET: memory lost ("Pr Error"): also clear the extension RAM.
P_MEMRESET:
        ?nc gosub SEL10
        c=0     w
        reg=c 0
        reg=c 1
        reg=c 2
        reg=c 3
        reg=c 4
        reg=c 5
        reg=c 6
        reg=c 7
        sel ram
        ldi $004                    ; displaced
        ?nc golong MEMRES_CONT

; ================================================ functions (stage 3)
; P_FUNC: every table function in plus mode that is not a stack, mode or
; WSIZE key. Entered from the dispatcher epilogue like an original
; routine: C = X bits 0-55, RAM 4 = the same and RAM 6 digits 1-0 = X
; bits 56-63 (the main loop saves LSTx from there at 0x0089), s7 = 1's
; complement, s8 = unsigned (set by 0x0581); word 1 of the function in
; CTRL digits 5-3 (P_DISP). Functions save LSTx themselves (SAVE_LX) and
; end at 0x008B (FN_EXIT, FN_EXIT_DROP), never with rtn, so all 4 return
; stack levels are theirs. Errors come before SAVE_LX (state unchanged).
; Status bits: s3, s6 scratch; s10 carry and s13 G results for the
; original flag setters 0x03B2/0x03BF/0x03C2.
P_FUNC:
        set hex
        ?nc gosub SEL10
        c=reg 0
        rcr 3
        a=c     x                   ; A.x = word 1
        ?nc gosub FT_TAB            ; return address = the table
; Function table: word 1, then a golong to the handler; 0 ends it (RUP,
; word 1 = 0, never comes here: STACK_KEY takes it).
        dw $182
        ?nc golong F_ADD
        dw $17f
        ?nc golong F_SUB
        dw $24b
        ?nc golong F_AND
        dw $21a
        ?nc golong F_OR
        dw $237
        ?nc golong F_XOR
        dw $21f
        ?nc golong F_NOT
        dw $2b3
        ?nc golong F_SL
        dw $2b0
        ?nc golong F_SR
        dw $2ae
        ?nc golong F_ASR
        dw $2bc
        ?nc golong F_RL
        dw $2b7
        ?nc golong F_RR
        dw $2bb
        ?nc golong F_RLC
        dw $2b6
        ?nc golong F_RRC
        dw $2d6
        ?nc golong F_RLN
        dw $2da
        ?nc golong F_RRN
        dw $2d8
        ?nc golong F_RLCN
        dw $2d9
        ?nc golong F_RRCN
        dw $2ee
        ?nc golong F_LJ
        dw $3e2
        ?nc golong F_NB
        dw $312
        ?nc golong F_MASKL
        dw $313
        ?nc golong F_MASKR
        dw $218
        ?nc golong F_SB
        dw $224
        ?nc golong F_CB
        dw $0b0
        ?nc golong F_CHS
        dw $19d
        ?nc golong F_ABS
        dw $1ce
        ?nc golong F_MUL
        dw $1de
        ?nc golong F_DBLX
        dw $1b1
        ?nc golong F_DIV
        dw $1c2
        ?nc golong F_RMD
        dw $1af
        ?nc golong F_DBLDIV
        dw $1ae
        ?nc golong F_DBLR
        dw $3b8
        ?nc golong F_STOI
        dw $399
        ?nc golong F_RCLI
        dw $3a0
        ?nc golong F_XEXI
        dw $353
        ?nc golong F_XEXII
        dw $3ab
        ?nc golong F_RCLII
        dw $3b1
        ?nc golong F_STOII
        dw $38d
        ?nc golong F_GTOI
        dw $38e
        ?nc golong F_GSBI
        dw $3bf
        ?nc golong F_DSZ
        dw $3be
        ?nc golong F_ISZ
        dw $00a
        ?nc golong F_CLRREG
        dw $385
        ?nc golong F_XEQY
        dw $371
        ?nc golong F_XNEY
        dw $37c
        ?nc golong F_XGTY
        dw $368
        ?nc golong F_XLEY
        dw $37f
        ?nc golong F_XLT0
        dw $36c
        ?nc golong F_XGT0
        dw $388
        ?nc golong F_XEQ0
        dw $374
        ?nc golong F_XNE0
        dw $22b
        ?nc golong F_BQ
        dw $155
        ?nc golong F_SQRT
        dw $000
FT_TAB:
        c=stk                       ; C[6:3] = the table
FT_L:
        cxisa                       ; C.x = word 1 of the entry
        ? c#0   x
        ?nc go  FN_ERR2             ; end of the table
        ? a#c   x
        ?nc go  FT_HIT
        c=c+1   m                   ; next entry (3 words)
        c=c+1   m
        c=c+1   m
        ?nc go  FT_L
FT_HIT:
        c=c+1   m
        goto c                      ; the golong of the entry
FN_ERR2:                            ; not implemented yet: Error 2
        c=0     w
        sel ram
        ?nc golong ERROR2

; FN_EXIT: unary end, the result in X (N, RAM 7 digits 1-0, extension).
FN_EXIT:
        c=0     w
        sel ram
        c=reg 7
        a=c     x                   ; A.x = X bits 56-63
        c=n
        ?nc golong MAIN_NOLX
; FN_EXIT_C: as FN_EXIT, flag 4 from s10 first.
FN_EXIT_C:
        c=0     w
        sel ram
        ?nc gosub FLAG_C
        ?nc go  FN_EXIT
; FN_EXIT_DROP: binary end, the result in X's slots: Y <- Z, Z <- T.
FN_EXIT_DROP:
        ?nc gosub EXT_DROP
        c=0     w
        sel ram
        c=reg 7
        a=c     x
        c=n
        ?nc gosub STACK_DROP        ; keeps A; C = result bits 0-55
        ?nc golong MAIN_NOLX

; SAVE_LX: LSTx <- X, all 128 bits (bits 0-63 as the main loop would at
; 0x0089). Afterwards RAM 4 is free scratch. Keeps A; uses B, C, G, M.
; RAM select ends 0x010.
SAVE_LX:
        c=0     w
        sel ram
        ?nc gosub SAVE_LASTX
        ?nc gosub SEL10
        c=reg 1
        reg=c 5                     ; LSTx bits 64-119
        c=reg 6
        pt= 0
        g=c
        pt= 8
        c=g
        reg=c 6                     ; LSTx bits 120-127
        rtn

; EXT_DROP: Y.ext <- Z.ext, Z.ext <- T.ext (T kept). Uses C, G. RAM
; select ends 0x010.
EXT_DROP:
        ?nc gosub SEL10
        c=reg 3
        reg=c 2
        c=reg 4
        reg=c 3
        c=reg 6
        pt= 4
        g=c
        pt= 2
        c=g
        pt= 6
        g=c
        pt= 4
        c=g
        reg=c 6
        rtn

; ------------------------------------------------- 128-bit helpers
; KW: A.x = k = W - 64 (1..64), rest of A 0. Uses C. RAM select 0x010.
KW:
        c=0     w
        ldi $010
        sel ram
        c=reg 0
        a=0     w
        pt= 1
        a=c     wp
        c=0     w
        ldi $040
        a=a-c   x
        rtn

; POW2: A.x = j (0..64) -> 2^j as a 64-bit value in the 56 + 8 split:
; C = bits 0-55 part (0 if j >= 56), B = bits 56-63 part in digits 2-0
; (2^(j-56); 0x100 for j = 64). Uses A. Hex mode.
POW2:
        b=0     w
        c=0     w
        ldi $038
        ? a<c   x
        ?c go   PW_W
        a=a-c   x                   ; j - 56 (0..8)
        c=0     w
        c=c+1   w
PW_BL:
        ? a#0   x
        ?nc go  PW_BD
        c=c+c   w
        a=a-1   x
        ?nc go  PW_BL
PW_BD:
        bc ex   w                   ; B = 2^(j-56), C = 0
        rtn
PW_W:
        c=0     w
        ldi $004
        bc ex   w                   ; B = 4
        c=0     w
        c=c+1   w
PW_D:
        ? a<b   x
        ?c go   PW_B
        rcr 13                      ; one digit up
        a=a-b   x
        ?nc go  PW_D
PW_B:
        ? a#0   x
        ?nc go  PW_E
        c=c+c   w
        a=a-1   x
        ?nc go  PW_B
PW_E:
        b=0     w
        rtn

; MASK64: A.x = j (0..64) -> 2^j - 1 in the 56 + 8 split: C (bits 0-55
; part), B (bits 56-63 part, digits 1-0). Uses A.
MASK64:
        ?nc gosub POW2
        ? c#0   w
        ?nc go  MK_B
        c=c-1   w
        rtn
MK_B:
        bc ex   w
        c=c-1   w
        bc ex   w
        c=0     w
        c=c-1   w
        rtn

; TRUNCX: X = X mod 2^W (bits 64-127 only; W > 64). TRK: X = X mod
; 2^(64 + A.x). Uses A, B, C. RAM select ends 0x010.
TRUNCX:
        ?nc gosub KW
TRK:
        ?nc gosub MASK64
        a=c     w
        c=reg 1
        c=c&a
        reg=c 1
        c=0     w
        c=c-1   w
        pt= 1
        c=b     wp                  ; ones except the byte mask
        a=c     w
        c=reg 6
        c=c&a
        reg=c 6
        rtn

; XBIT: A.x = j (0..63): C.x = 1 if bit 64 + j of X is set, else C = 0.
; TOPBIT: the same for bit W - 1, TOPBITY for bit W - 1 of Y. Uses A, B,
; C. RAM select ends 0x010.
TOPBIT:
        ?nc gosub KW
        a=a-1   x
XBIT:
        ?nc gosub POW2
        a=c     w
        ?nc gosub SEL10
        c=reg 1
        c=c&a
        ? c#0   w
        ?c go   XB_1
        c=reg 6
XB_B:
        ab ex   w
        c=c&a
        ? c#0   w
        ?nc rtn
XB_1:
        c=0     w
        c=c+1   w
        rtn
TOPBITY:
        ?nc gosub KW
        a=a-1   x
        ?nc gosub POW2
        a=c     w
        c=reg 2
        c=c&a
        ? c#0   w
        ?c go   XB_1
        c=reg 6
        rcr 2                       ; Y bits 120-127 to digits 1-0
        ?nc go  XB_B

; SETTOP: X = X or 2^(W-1). Uses A, B, C. RAM select ends 0x010.
SETTOP:
        ?nc gosub KW
        a=a-1   x
        ?nc gosub POW2
        a=c     w
        c=reg 1
        c=c|a
        reg=c 1
        ab ex   w
        c=reg 6
        c=c|a
        reg=c 6
        rtn

; NOT128: X = not X over all 128 bits. Uses C. RAM select ends 0x010.
NOT128:
        c=n
        c=-c-1  w
        n=c
        m=c
        c=0     w
        sel ram
        c=reg 7
        pt= 1
        c=-c-1  wp
        reg=c 7
        ?nc gosub SEL10
        c=reg 1
        c=-c-1  w
        reg=c 1
        c=reg 6
        pt= 1
        c=-c-1  wp
        reg=c 6
        rtn

; ADD128: X = X + Y + s6 over 128 bits; s6 = carry out of bit 127.
; INCX: X = X + 1, s6 = carry out. DECX: X = X - 1 (mod 2^128).
; Uses A, B, C. RAM select ends 0x010.
ADD128:
        c=0     w
        sel ram
        c=reg 0
        a=c     w                   ; Y bits 0-55
        c=n
        ?nc gosub ADDW
        n=c
        m=c
        c=reg 7
        rcr 3
        a=0     w
        pt= 1
        a=c     wp                  ; Y bits 56-63
        c=reg 7
        ?nc gosub ADDB
        reg=c 7
        ?nc gosub SEL10
        c=reg 2
        a=c     w                   ; Y bits 64-119
        c=reg 1
        ?nc gosub ADDW
        reg=c 1
        c=reg 6
        rcr 2
        a=0     w
        pt= 1
        a=c     wp                  ; Y bits 120-127
        c=reg 6
        ?nc gosub ADDB
        reg=c 6
        rtn
INCX:
        s=1     6
        c=0     w
        sel ram
        a=0     w
        c=n
        ?nc gosub ADDW
        n=c
        m=c
        c=reg 7
        ?nc gosub ADDB
        reg=c 7
        ?nc gosub SEL10
        a=0     w
        c=reg 1
        ?nc gosub ADDW
        reg=c 1
        c=reg 6
        ?nc gosub ADDB
        reg=c 6
        rtn
DECX:
        ?nc gosub NOT128            ; not(not X + 1) = X - 1
        ?nc gosub INCX
        ?nc go  NOT128

; ADDW: C = A + C + s6 (56 bits), s6 = carry out. Uses C.
ADDW:
        c=a+c   w
        ?c go   AW_C
        ? s=1   6
        ?nc rtn
        c=c+1   w
        ?c rtn                      ; carry out, s6 stays 1
        s=0     6
        rtn
AW_C:
        ? s=1   6
        ?nc go  AW_C1
        c=c+1   w                   ; cannot carry again
AW_C1:
        s=1     6
        rtn

; ADDB: C = a register with a byte in digits 1-0, A.x = a byte: digits
; 1-0 = byte + A.x + s6 (mod 256), other digits kept; s6 = carry out.
; Uses B.
ADDB:
        bc ex   w
        c=0     w
        pt= 1
        c=b     wp
        c=a+c   x
        ? s=1   6
        ?nc go  AB_0
        c=c+1   x
AB_0:
        s=0     6
        pt= 2
        ? c#0   p
        ?nc go  AB_1
        s=1     6
AB_1:
        bc ex   w
        pt= 1
        c=b     wp
        rtn

; SHR1X: X = X / 2 over 128 bits, s6 = bit in (to bit 127). Out: s6 =
; old bit 0. Uses A, B, C. RAM select ends 0x000.
SHR1X:
        ?nc gosub SEL10
        c=reg 6
        ?nc gosub SHRB              ; bits 120-127
        reg=c 6
        c=reg 1
        ?nc gosub SHRW              ; bits 64-119
        reg=c 1
        c=0     w
        sel ram
        c=reg 7
        ?nc gosub SHRB              ; bits 56-63
        reg=c 7
        c=n
        ?nc gosub SHRW              ; bits 0-55
        n=c
        m=c
        rtn

; SHRW: C = (s6 2^56 + C) / 2 (56 bits), s6 = old bit 0. Uses A.
SHRW:
        a=c     w
        c=0     w
        pt= 0
        ac ex   p                   ; C = low nibble L, A = word - L
        a sr    w                   ; A = word / 16
        ? s=1   6
        ?nc go  SW_0
        pt= 13
        a=a+1   p                   ; bit in at bit 52 (55 after * 8)
SW_0:
        s=0     6
        c=c+c   x
        c=c+c   x
        c=c+c   x                   ; 8 L: digit 0 = 8 (L odd) or 0
        pt= 0
        ? c#0   p
        ?nc go  SW_1
        s=1     6
SW_1:
        c sr    x                   ; L / 2
        ac ex   w
        c=c+c   w
        c=c+c   w
        c=c+c   w                   ; (word / 16) * 8
        c=a+c   w
        rtn

; SHRB: C = a register with a byte in digits 1-0: byte = (s6 2^8 +
; byte) / 2, other digits kept; s6 = old bit 0. Uses A, B.
SHRB:
        bc ex   w
        c=0     w
        pt= 1
        c=b     wp
        ? s=1   6
        ?nc go  SB_0
        pt= 2
        c=c+1   p                   ; + 2^8
SB_0:
        s=0     6
        a=c     w
        c=0     w
        c=c+1   w
        c=c&a
        ? c#0   w
        ?nc go  SB_1
        s=1     6
SB_1:
        ac ex   w
        c=c+c   x
        c=c+c   x
        c=c+c   x
        c sr    x                   ; (s6 2^8 + byte) / 2
        bc ex   w
        pt= 1
        c=b     wp
        rtn

; ROT1: rotate X one bit within W bits. s3 = right, s13 = through the
; carry; s10 = carry in and out. Uses A, B, C, G, s6. RAM select ends
; 0x000 or 0x010.
ROT1:
        ? s=1   3
        ?c go   R1_R
        ?nc gosub TOPBIT            ; C.x = bit W - 1
        a=c     x                   ; bit in: the top bit ...
        ? s=1   13
        ?nc go  R1_L
        a=0     x                   ; ... or the carry
        ? s=1   10
        ?nc go  R1_L
        a=a+1   x
R1_L:
        s=0     10                  ; new carry = the top bit
        ? c#0   x
        ?nc go  R1_L1
        s=1     10
R1_L1:
        ?nc gosub SHL1XC
        ?nc go  TRUNCX              ; drop bit W
R1_R:
        s=0     6
        ?nc gosub SHR1X             ; s6 = old bit 0
        ? s=1   13
        ?c go   R1_RC
        ? s=1   6                   ; RR: bit 0 to bit W - 1
        ?nc go  R1_RS
        ?nc gosub SETTOP
        ?nc go  R1_RS
R1_RC:
        ? s=1   10                  ; RRC: the carry to bit W - 1
        ?nc go  R1_RS
        ?nc gosub SETTOP
R1_RS:
        s=0     10
        ? s=1   6
        ?nc rtn
        s=1     10
        rtn

; GETC: s10 = flag 4 (carry). Uses A, C. RAM select ends 0x000.
GETC:
        c=0     w
        sel ram
        c=reg 6
        rcr 7                       ; digit 7 (flags 4-7) to digit 0
        a=0     w
        a=a+1   w
        c=c&a
        s=0     10
        ? c#0   w
        ?nc rtn
        s=1     10
        rtn

; COPYYX: X <- Y, all 128 bits. Uses A, C. RAM select ends 0x010.
COPYYX:
        c=0     w
        sel ram
        c=reg 0
        n=c
        m=c
        c=reg 7
        a=c     w
        rcr 3
        pt= 1
        a=c     wp
        ac ex   w
        reg=c 7
        ?nc gosub SEL10
        c=reg 2
        reg=c 1
        c=reg 6
        a=c     w
        rcr 2
        pt= 1
        a=c     wp
        ac ex   w
        reg=c 6
        rtn

; PUTXL: X bits 0-55 = C, bits 56-63 = B digits 1-0. PUTXE: X bits
; 64-119 = C, bits 120-127 = B digits 1-0. Use A, C.
PUTXL:
        n=c
        m=c
        c=0     w
        sel ram
        c=reg 7
        pt= 1
        c=b     wp
        reg=c 7
        rtn
PUTXE:
        a=c     w
        ?nc gosub SEL10
        ac ex   w
        reg=c 1
        c=reg 6
        pt= 1
        c=b     wp
        reg=c 6
        rtn

; ONESX: X = 2^j - 1, A.x = j (0..128). Uses A, B, C.
ONESX:
        c=0     w
        ldi $040
        ? a<c   x
        ?c go   OX_LO
        a=a-c   x
        ?nc gosub MASK64
        ?nc gosub PUTXE
        c=0     w
        c=c-1   w
        bc ex   w
        c=0     w
        ldi $0ff
        bc ex   w                   ; B = ff, C = all ones
        ?nc go  PUTXL
OX_LO:
        ?nc gosub MASK64
        ?nc gosub PUTXL
        c=0     w
        b=0     w
        ?nc go  PUTXE

; PUTN: C.x -> CTRL digits 5-3. GETN: A.x <- CTRL digits 5-3 (rest of A
; 0). Use A, C. RAM select ends 0x010.
PUTN:
        a=c     w
        ?nc gosub SEL10
        c=reg 0
        rcr 3
        ac ex   x
        rcr 11
        reg=c 0
        rtn
GETN:
        ?nc gosub SEL10
        c=reg 0
        rcr 3
        a=0     w
        a=c     x
        rtn

; COUNTX: n = |X| in the current complement mode, as a count. In: A.x =
; limit (up to 129). Out: C = n if n <= limit, else Error 2 (no return,
; state unchanged). Uses A, B, C; CTRL digits 5-3.
COUNTX:
        ac ex   w
        ?nc gosub PUTN              ; CTRL digits 5-3 = limit
        ?nc gosub CXCORE
        ? s=1   6
        ?c go   CX_ERR
        bc ex   w                   ; B = n
        ?nc gosub GETN              ; A.x = limit
        c=b     w
        ? a<c   w                   ; limit < n ?
        ?c go   CX_ERR
        rtn
CX_ERR:
        ?nc golong FN_ERR2

; CXCORE: C = |X| in the current complement mode if below 2^56, s6 = 0;
; s6 = 1 if not. X unchanged. Uses A, B, C.
CXCORE:
        s=0     6
        ? s=1   8
        ?c go   CX_P                ; unsigned: never negative
        ?nc gosub TOPBIT
        ? c#0   x
        ?c go   CX_N
CX_P:
        ?nc gosub SEL10             ; X >= 0: bits 56-127 must be 0
        c=reg 1
        ? c#0   w
        ?c go   CC_BIG
        c=reg 6
        rcr 2
        pt= 11
        c=0     wp                  ; bits 120-127 at digits 13-12
        ? c#0   w
        ?c go   CC_BIG
        c=0     w
        sel ram
        c=reg 7
        rcr 2
        pt= 11
        c=0     wp                  ; bits 56-63
        ? c#0   w
        ?c go   CC_BIG
        c=n
        ?nc go  CC_OK
CX_N:
        ?nc gosub KW                ; X < 0: bits 56..W-1 must be all ones
        ?nc gosub MASK64            ;  C, B = 2^k - 1
        a=c     w
        c=reg 1
        ? a#c   w
        ?c go   CC_BIG
        c=reg 6
        rcr 2
        pt= 11
        c=0     wp
        rcr 12                      ; C = bits 120-127
        ab ex   w
        ? a#c   w
        ?c go   CC_BIG
        c=0     w
        sel ram
        c=reg 7
        rcr 2
        pt= 11
        c=0     wp
        rcr 12                      ; C = bits 56-63
        a=c     w
        c=0     w
        ldi $0ff
        ? a#c   w
        ?c go   CC_BIG
        c=n
        c=-c-1  w                   ; 1's: n = not X
        ? s=1   7
        ?c go   CC_OK
        c=c+1   w                   ; 2's: n = not X + 1
        ?c go   CC_BIG
CC_OK:
        rtn
CC_BIG:
        s=1     6
        rtn

; ------------------------------------------------------ + and -
; X <- Y + X or Y - X (drop). Carry: result beyond W bits (add) or
; borrow (sub); 1's complement: end-around carry or borrow. G: carry
; (unsigned) or signed overflow from the signs of X, Y and the result
; (Model16C.add/sub). The signs go to RAM 4 digits 0 (X) and 1 (Y).
F_SUB:
        s=1     3
        ?nc go  FA_0
F_ADD:
        s=0     3
FA_0:
        ?nc gosub SAVE_LX
        ?nc gosub TOPBIT
        a=c     w
        c=0     w
        sel ram
        ac ex   w
        reg=c 4                     ; RAM 4 digit 0 = sign of X
        ?nc gosub TOPBITY
        rcr 13
        a=c     w
        c=0     w
        sel ram
        c=reg 4
        c=a+c   w
        reg=c 4                     ; digit 1 = sign of Y
        s=0     6
        ? s=1   3
        ?nc go  FA_1
        ?nc gosub NOT128            ; Y - X = Y + not X + 1 (128 bits)
        s=1     6
FA_1:
        ?nc gosub ADD128            ; s6 = carry out of bit 127
        s=0     10
        ? s=1   3
        ?c go   FA_SB
        ? s=1   6
        ?c go   FA_C1
        ?nc gosub KW
        c=0     w
        ldi $040
        ? a#c   x
        ?nc go  FA_TR               ; W = 128: only the carry out
        ?nc gosub XBIT              ; bit W of the sum
        ? c#0   x
        ?nc go  FA_TR
FA_C1:
        s=1     10
        ?nc go  FA_TR
FA_SB:
        ? s=1   6                   ; carry out: no borrow
        ?c go   FA_TR
        s=1     10
FA_TR:
        ?nc gosub TRUNCX
        ? s=1   7                   ; 1's complement with carry/borrow:
        ?nc go  FA_G                ;  end-around
        ? s=1   10
        ?nc go  FA_G
        ? s=1   3
        ?c go   FA_DEC
        ?nc gosub INCX
        ?nc go  FA_G
FA_DEC:
        ?nc gosub DECX
        ?nc gosub TRUNCX
FA_G:
        s=0     13
        ? s=1   8
        ?nc go  FA_SG
        ? s=1   10                  ; unsigned: G = carry
        ?nc go  FA_END
        s=1     13
        ?nc go  FA_END
FA_SG:
        ?nc gosub TOPBIT            ; C.x = sign of the result
        a=c     w
        c=0     w
        sel ram
        c=reg 4
        rcr 1                       ; digit 0 = sign of Y
        pt= 0
        ? a#c   p                   ; result sign <> Y sign ?
        ?nc go  FA_END
        rcr 13
        a=c     w
        a sr    w                   ; A digit 0 = sign of Y, C: of X
        ? a#c   p
        ?c go   FA_NE
        ? s=1   3                   ; same signs: overflow for +
        ?c go   FA_END
        s=1     13
        ?nc go  FA_END
FA_NE:
        ? s=1   3                   ; different signs: overflow for -
        ?nc go  FA_END
        s=1     13
FA_END:
        c=0     w
        sel ram
        ?nc gosub FLAGS_CG
        ?nc golong FN_EXIT_DROP

; ------------------------------------------------------ logic
; AND, OR, XOR: X <- Y op X (drop); NOT: X <- not X (W bits). No flags.
F_OR:
        s=1     3
        s=0     6
        ?nc go  FL_0
F_XOR:
        s=0     3
        s=1     6
        ?nc go  FL_0
F_AND:
        s=0     3
        s=0     6
FL_0:
        ?nc gosub SAVE_LX
        ?nc gosub LOGIC4
        ?nc golong FN_EXIT_DROP
F_NOT:
        ?nc gosub SAVE_LX
        ?nc gosub NOT128
        ?nc gosub TRUNCX
        ?nc golong FN_EXIT

; LOGIC4: X = Y op X over the four parts (op: s3 OR, s6 XOR, else AND).
; Uses A, B, C, M. RAM select ends 0x010.
LOGIC4:
        c=0     w
        sel ram
        c=reg 0
        a=c     w
        c=n
        ?nc gosub LOP
        n=c
        m=c
        c=reg 7
        rcr 3
        a=0     w
        pt= 1
        a=c     wp
        c=reg 7
        ?nc gosub LOPB
        reg=c 7
        ?nc gosub SEL10
        c=reg 2
        a=c     w
        c=reg 1
        ?nc gosub LOP
        reg=c 1
        c=reg 6
        rcr 2
        a=0     w
        pt= 1
        a=c     wp
        c=reg 6
        ?nc gosub LOPB
        reg=c 6
        rtn

; LOPB: as LOP for a byte in digits 1-0 of C (A.x = the other byte);
; other digits of C kept. Uses B.
LOPB:
        bc ex   w
        c=0     w
        pt= 1
        c=b     wp
        ?nc gosub LOP
        bc ex   w
        pt= 1
        c=b     wp
        rtn

; LOP: C = A op C. XOR = (A or C) - (A and C). Uses A, M (XOR).
LOP:
        ? s=1   3
        ?c go   LP_OR
        ? s=1   6
        ?c go   LP_X
        c=c&a
        rtn
LP_OR:
        c=c|a
        rtn
LP_X:
        m=c
        c=c&a
        c<>m                        ; M = A and C
        c=c|a
        a=c     w
        c=m
        c=a-c   w
        rtn

; ------------------------------------------------- shifts, rotates
; SL: carry = bit W - 1, X << 1. SR: carry = bit 0, X >> 1. ASR: as SR,
; the sign bit stays (1's and 2's complement).
F_SL:
        ?nc gosub SAVE_LX
        ?nc gosub TOPBIT
        s=0     10
        ? c#0   x
        ?nc go  FSL_1
        s=1     10
FSL_1:
        ?nc gosub SHL1X
        ?nc gosub TRUNCX
        ?nc golong FN_EXIT_C
F_SR:
        ?nc gosub SAVE_LX
        s=0     3
        ?nc go  FSR_1
F_ASR:
        ?nc gosub SAVE_LX
        s=0     3
        ? s=1   8
        ?c go   FSR_1
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc go  FSR_1
        s=1     3                   ; negative: keep the sign bit
FSR_1:
        s=0     6
        ?nc gosub SHR1X
        s=0     10
        ? s=1   6
        ?nc go  FSR_2
        s=1     10
FSR_2:
        ? s=1   3
        ?nc go  FSR_3
        ?nc gosub SETTOP
FSR_3:
        ?nc golong FN_EXIT_C

; RL, RR, RLC, RRC: one bit (ROT1). RLN, RRN, RLCN, RRCN: Y rotated |X|
; times (drop); |X| up to W, through the carry up to W + 1.
F_RL:
        s=0     3
        s=0     13
        ?nc go  FR_1
F_RR:
        s=1     3
        s=0     13
        ?nc go  FR_1
F_RLC:
        s=0     3
        s=1     13
        ?nc go  FR_1
F_RRC:
        s=1     3
        s=1     13
FR_1:
        ?nc gosub SAVE_LX
        ?nc gosub GETC
        ?nc gosub ROT1
        ?nc golong FN_EXIT_C
F_RLN:
        s=0     3
        s=0     13
        ?nc go  FRN
F_RRN:
        s=1     3
        s=0     13
        ?nc go  FRN
F_RLCN:
        s=0     3
        s=1     13
        ?nc go  FRN
F_RRCN:
        s=1     3
        s=1     13
FRN:
        ?nc gosub KW
        c=0     w
        ldi $040
        a=a+c   x                   ; W
        ? s=1   13
        ?nc go  FRN_1
        a=a+1   x
FRN_1:
        ?nc gosub COUNTX            ; C = n, CTRL count = the ring
        bc ex   w                   ; B = n
        ?nc gosub GETN              ; A.x = the ring (W, or W + 1)
        a=a-b   x
        ? a<b   x                   ; ring - n < n: the other way round
        ?c go   FRN_S
        c=0     w                   ; (flag: same way)
        ?nc go  FRN_F
FRN_S:
        b=a     w                   ; B = ring - n steps
        ? s=1   3
        ?c go   FRN_R
        s=1     3
        ?nc go  FRN_T
FRN_R:
        s=0     3
FRN_T:
        c=0     w
        c=c+1   w                   ; (flag: turned)
FRN_F:
        rcr 8                       ; flag to CTRL digit 6
        a=c     w
        ?nc gosub SEL10
        c=reg 0
        pt= 6
        ac ex   p
        reg=c 0
        c=b     w
        ?nc gosub PUTN              ; steps
        ?nc gosub SAVE_LX
        ?nc gosub COPYYX
        ?nc gosub GETC
FRN_L:
        ?nc gosub GETN
        ? a#0   x
        ?nc go  FRN_E
        a=a-1   x
        ac ex   x
        rcr 11
        reg=c 0                     ; count - 1
        ?nc gosub ROT1
        ?nc go  FRN_L
FRN_E:
        ? s=1   13                  ; through the carry: the ring is the
        ?c go   FRN_G               ;  same both ways
        ?nc gosub SEL10
        c=reg 0
        pt= 6
        ? c#0   p
        ?nc go  FRN_G
        s=0     10                  ; turned: the carry of the asked way
        ? s=1   3
        ?nc go  FRN_H
        c=n                         ; RLN: the bit that went round = bit 0
        a=0     w
        a=a+1   w
        c=c&a
        ? c#0   w
        ?nc go  FRN_G
        s=1     10
        ?nc go  FRN_G
FRN_H:
        ?nc gosub TOPBIT            ; RRN: = bit W - 1
        ? c#0   x
        ?nc go  FRN_G
        s=1     10
FRN_G:
        c=0     w
        sel ram
        ?nc gosub FLAG_C
        ?nc golong FN_EXIT_DROP

; ------------------------------------------------------ bit functions
; LJ: Y <- X shifted left until bit W - 1 is set, X <- the shift count
; (stack lifts). X = 0: Y = 0, X = 0.
F_LJ:
        ?nc gosub SAVE_LX
        ?nc gosub BITLEN            ; A.x = bits (0 for X = 0)
        ? a#0   x
        ?nc go  LJ_N
        ab ex   w                   ; B.x = bits
        ?nc gosub KW
        c=0     w
        ldi $040
        a=a+c   x
        a=a-b   x                   ; n = W - bits
LJ_N:
        c=0     w
        sel ram
        ac ex   x
        reg=c 4                     ; RAM 4 = n (the new X)
        ?nc gosub PUTN              ; loop count
LJ_L:
        ?nc gosub GETN
        ? a#0   x
        ?nc go  LJ_D
        a=a-1   x
        ac ex   x
        rcr 11
        reg=c 0
        ?nc gosub SHL1X
        ?nc go  LJ_L
LJ_D:
        c=0     w
        sel ram
        c=n
        ?nc gosub STACK_LIFT        ; T <- Z, Z <- Y, Y <- X (bits 0-63)
        ?nc gosub SEL10
        ?nc gosub EXT_LIFT
        c=0     w
        sel ram
        c=reg 4
        b=0     w
        ?nc gosub PUTXL             ; X = n
        c=0     w
        b=0     w
        ?nc gosub PUTXE
        ?nc golong FN_EXIT

; #B: X <- the number of 1 bits in X.
F_NB:
        ?nc gosub SAVE_LX
        b=0     w                   ; B.x = count
        c=n
        ?nc gosub NB_CNT
        c=0     w
        sel ram
        c=reg 7
        rcr 2
        pt= 11
        c=0     wp                  ; bits 56-63 at digits 13-12
        ?nc gosub NB_CNT
        ?nc gosub SEL10
        c=reg 1
        ?nc gosub NB_CNT
        c=reg 6
        rcr 2
        pt= 11
        c=0     wp
        ?nc gosub NB_CNT
        bc ex   w                   ; C = count
        b=0     w
        ?nc gosub PUTXL
        c=0     w
        b=0     w
        ?nc gosub PUTXE
        ?nc golong FN_EXIT

; NB_CNT: B.x = B.x + the number of 1 bits in C. Uses C.
NB_CNT:
        ? c#0   w
        ?nc rtn
        c=c+c   w
        ?nc go  NB_CNT
        bc ex   w
        c=c+1   x
        bc ex   w
        ?nc go  NB_CNT

; MASKR: X <- 2^n - 1; MASKL: the top n bits of W; n = |X| up to W.
F_MASKR:
        s=0     3
        ?nc go  FMK
F_MASKL:
        s=1     3
FMK:
        ?nc gosub KW
        c=0     w
        ldi $040
        a=a+c   x                   ; limit W
        ?nc gosub COUNTX
        ?nc gosub PUTN
        ?nc gosub SAVE_LX
        ?nc gosub GETN              ; A.x = n
        ? s=1   3
        ?nc go  FMK_1
        ab ex   w                   ; MASKL: not (2^(W-n) - 1) within W
        ?nc gosub KW
        c=0     w
        ldi $040
        a=a+c   x
        a=a-b   x                   ; A.x = W - n
        ?nc gosub ONESX
        ?nc gosub NOT128
        ?nc gosub TRUNCX
        ?nc golong FN_EXIT
FMK_1:
        ?nc gosub ONESX
        ?nc golong FN_EXIT

; SB, CB: X <- Y with bit n set / cleared (drop); n = |X| up to W - 1.
F_SB:
        s=0     3
        ?nc go  FSB
F_CB:
        s=1     3
FSB:
        ?nc gosub KW
        c=0     w
        ldi $03f
        a=a+c   x                   ; limit W - 1
        ?nc gosub COUNTX
        ?nc gosub PUTN
        ?nc gosub SAVE_LX
        ?nc gosub COPYYX
        ?nc gosub GETN              ; A.x = n
        c=0     w
        ldi $040
        ? a<c   x
        ?c go   FSB_LO
        a=a-c   x
        ?nc gosub POW2              ; bit n - 64
        ?nc gosub MKOP
        ?nc gosub SEL10
        c=reg 1
        ?nc gosub OPM
        reg=c 1
        ab ex   w
        c=reg 6
        ?nc gosub OPM
        reg=c 6
        ?nc golong FN_EXIT_DROP
FSB_LO:
        ?nc gosub POW2              ; bit n
        ?nc gosub MKOP
        c=n
        ?nc gosub OPM
        n=c
        m=c
        ab ex   w
        c=0     w
        sel ram
        c=reg 7
        ?nc gosub OPM
        reg=c 7
        ?nc golong FN_EXIT_DROP

; MKOP: masks C (56-bit part), B (byte part) -> A = C, B kept; with s3
; (clear) both complemented. OPM: C = C or A (s3 = 0), C and A (s3 = 1).
MKOP:
        a=c     w
        ? s=1   3
        ?nc rtn
        c=-c-1  w
        a=c     w
        c=b     w
        c=-c-1  w
        bc ex   w
        rtn
OPM:
        ? s=1   3
        ?c go   OM_A
        c=c|a
        rtn
OM_A:
        c=c&a
        rtn

; ------------------------------------------------------ CHS, ABS
; CHS: 2's: X <- -X, G set for the most negative number (unchanged);
; unsigned: X <- -X mod 2^W, G set unless X = 0; 1's: X <- not X, G
; cleared. ABS: negative X as CHS; otherwise unchanged, G cleared.
F_CHS:
        ?nc gosub SAVE_LX
        s=0     13
        ? s=1   7
        ?c go   FC_1S
        s=0     3
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc go  FC_N
        s=1     3                   ; X negative
FC_N:
        ?nc gosub NEG2X             ; s6 = X was 0
        ? s=1   8
        ?c go   FC_U
        ? s=1   3                   ; 2's: negative before and after:
        ?nc go  FC_G                ;  the most negative number
FC_MN:
        ?nc gosub TOPBIT            ; 2's: still negative = most negative
        ? c#0   x
        ?nc go  FC_G
        s=1     13
        ?nc go  FC_G
FC_U:
        ? s=1   6
        ?c go   FC_G
        s=1     13
        ?nc go  FC_G
F_ABS:
        ?nc gosub SAVE_LX
        s=0     13
        ? s=1   8
        ?c go   FC_G                ; unsigned: unchanged
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc go  FC_G                ; not negative: unchanged
        ? s=1   7
        ?c go   FC_1S
        ?nc gosub NEG2X
        ?nc go  FC_MN
FC_1S:
        ?nc gosub NOT128
        ?nc gosub TRUNCX
FC_G:
        c=0     w
        sel ram
        ?nc gosub FLAG_G
        ?nc golong FN_EXIT

; NEG2X: X = -X mod 2^W; s6 = 1 if X was 0. Uses A, B, C.
NEG2X:
        ?nc gosub NOT128
        ?nc gosub INCX
        ?nc go  TRUNCX

; ================================================ functions (stage 4)
; *, DBLX, /, RMD, DBL/, DBLR on magnitudes, in fixed slots: X = the
; remainder or the high product, Y = dividend and quotient or multiplier
; and low product, V = the divisor or multiplicand magnitude in LSTx (s10
; = 0) or, for DBL/ and DBLR, in Z (s10 = 1, LSTx must stay unchanged
; until no error can come). These functions do not use SAVE_LX: LSTx is
; written from V and the sign at the end, so RAM 4 is scratch from the
; start: digit 0 = sign of X, 1 = sign of Y (the dividend), 2 = variant,
; 3 = result negative, 4 = remainder <> 0. Errors restore the state
; first. More nesting than stage 3: routines marked "top level" must be
; called from the function itself (NEGX nests 4 deep).

; ERROR0, ERROR3, ERROR4: Error 0 (division by zero, quotient too
; large), 3 (no such register), 4 (no such label). ERRPT: Error pt.
ERROR3:
        pt= 3
        ?nc go  ERRPT
ERROR4:
        pt= 4
        ?nc go  ERRPT
ERROR0:
        pt= 0
ERRPT:
        c=0     w
        sel ram
        ?nc golong ERR_N

; R4: C = RAM 4 (RAM select 0x000).
R4:
        c=0     w
        sel ram
        c=reg 4
        rtn

; SIGNS: RAM 4 digit 0 = sign of X, digit 1 = sign of Y (bit W - 1, 1's
; and 2's complement); digit 3 = their xor. Other digits kept.
SIGNS:
        ? s=1   8
        ?c rtn
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc go  SG_1
        ?nc gosub R4
        pt= 0
        lc 1
        reg=c 4
SG_1:
        ?nc gosub TOPBITY
        ? c#0   x
        ?nc go  SG_2
        ?nc gosub R4
        pt= 1
        lc 1
        reg=c 4
SG_2:
        ?nc gosub R4
        a=c     w
        a sr    w
        pt= 0
        ? a#c   p
        ?nc rtn
        pt= 3
        lc 1
        reg=c 4
        rtn

; NOTW: X = not X within W bits. NEGX: X = -X in the complement mode
; (1's: not X). Top level.
NOTW:
        ?nc gosub NOT128
        ?nc go  TRUNCX
NEGX:
        ? s=1   7
        ?c go   NOTW
        ?nc go  NEG2X

; NEGXS: X = -X if RAM 4 digit 0 (sign of X) is set. Top level.
NEGXS:
        ?nc gosub R4
        pt= 0
        ? c#0   p
        ?c go   NEGX
        rtn

; CLRX: X = 0. CLRTOP: X = X and not 2^(W-1).
CLRX:
        c=0     w
        b=0     w
        ?nc gosub PUTXL
        c=0     w
        b=0     w
        ?nc go  PUTXE
CLRTOP:
        ?nc gosub KW
        a=a-1   x
        ?nc gosub POW2
        c=-c-1  w
        a=c     w
        c=reg 1
        c=c&a
        reg=c 1
        c=b     w
        c=-c-1  w
        a=c     w
        c=reg 6
        c=c&a
        reg=c 6
        rtn

; ZDIVCHK: Error 0 if |X| = 0 (X = 0, or -0 in 1's complement); the
; state is unchanged either way. Top level.
ZDIVCHK:
        ?nc gosub BITLEN
        ? a#0   x
        ?nc go  ERROR0
        ? s=1   7
        ?nc rtn
        ?nc gosub NOTW
        ?nc gosub BITLEN
        ? a#0   x
        ?c go   NOTW                ; not -0: back, done
        ?nc gosub NOTW
        ?nc go  ERROR0

; SWAPXY, SWAPXZ, SWAPXL: X <-> Y, Z or LSTx, all 128 bits. Stack
; register i = 1, 2, 4: low part in RAM i - 1, bits 56-63 at RAM 7 digit
; 2i + 1, bits 64-119 in RAM 0x11 + i, bits 120-127 at RAM 0x16 digit
; 2i. Use A, B, C, G, M. RAM select ends 0x010.
SWAPXL:
        a=0     x
        a=a+1   x
        a=a+1   x
        ?nc go  SX_Z
SWAPXZ:
        a=0     x
SX_Z:
        a=a+1   x
        ?nc go  SX_Y
SWAPXY:
        a=0     x
SX_Y:
        a=a+1   x
SWAPX:
        c=0     w
        c=a+c   x
        m=c                         ; M = i
        c=c-1   x
        sel ram
        c=reg 0                     ; (c=data) RAM i - 1
        c<>n
        data=c                      ; bits 0-55
        c=0     w
        sel ram
        c=reg 7
        pt= 0
        g=c                         ; G = X bits 56-63
        c<>m
        a=c     w                   ; A = i
        c<>m
        pt= 1
SX_7:
        inc pt
        inc pt
        a=a-1   x
        ? a#0   x
        ?c go   SX_7                ; pt = 2i + 1
        c<>g
        pt= 0
        c=g
        reg=c 7                     ; bits 56-63
        c=m
        a=c     w
        c=0     w
        ldi $011
        a=a+c   x                   ; A.x = 0x11 + i
        c=0     w
        c=a+c   w
        sel ram
        c=reg 0                     ; (c=data) the other's bits 64-119
        bc ex   w
        c=reg 1                     ; X's (selects 0x011)
        bc ex   w
        reg=c 1
        c=0     w
        c=a+c   w
        sel ram
        c=b     w
        data=c                      ; bits 64-119
        c=reg 6
        pt= 0
        g=c                         ; G = X bits 120-127
        c<>m
        a=c     w                   ; A = i
        c<>m
        pt= 0
SX_6:
        inc pt
        inc pt
        a=a-1   x
        ? a#0   x
        ?c go   SX_6                ; pt = 2i
        c<>g
        pt= 0
        c=g
        reg=c 6                     ; bits 120-127
        c=n
        m=c
        rtn

; DROP1: Y <- Z, Z <- T (T kept), X unchanged. Uses A, B, C, G, M.
DROP1:
        ?nc gosub EXT_DROP
        c=0     w
        sel ram
        c=reg 7
        a=c     x
        c=n
        ?nc gosub STACK_DROP        ; keeps A; C = X bits 0-55
        n=c
        m=c
        c=reg 7
        pt= 1
        ac ex   wp                  ; X bits 56-63 back
        reg=c 7
        rtn


; SHL1YC: Y = 2 Y + A.x (0 or 1), A.x = old bit 127. Uses A.x, C, G.
; RAM select ends 0x010.
SHL1YC:
        set hex
        c=0     w
        sel ram
        c=reg 0
        c=c+c   w                   ; bits 0-55
        ?c go   SY_C1
        c=a+c   x
        a=0     x
        ?nc go  SY_N0
SY_C1:
        c=a+c   x
        a=0     x
        a=a+1   x
SY_N0:
        reg=c 0
        c=reg 7
        rcr 3
        ?nc gosub DBLB              ; bits 56-63
        c=reg 7
        pt= 3
        c=g
        reg=c 7
        ?nc gosub SEL10
        c=reg 2
        c=c+c   w                   ; bits 64-119
        ?nc go  SY_E0
        c=a+c   x
        a=0     x
        a=a+1   x
        ?nc go  SY_E1
SY_E0:
        c=a+c   x
        a=0     x
SY_E1:
        reg=c 2
        c=reg 6
        rcr 2
        ?nc gosub DBLB              ; bits 120-127
        c=reg 6
        pt= 2
        c=g
        reg=c 6
        rtn

; SHR1Y: Y = Y / 2 over 128 bits, s6 = bit in (to bit 127). Out: s6 =
; old bit 0. Uses A, B, C. RAM select ends 0x000.
SHR1Y:
        ?nc gosub SEL10
        c=reg 6
        rcr 2
        ?nc gosub SHRB              ; bits 120-127
        rcr 12
        reg=c 6
        c=reg 2
        ?nc gosub SHRW              ; bits 64-119
        reg=c 2
        c=0     w
        sel ram
        c=reg 7
        rcr 3
        ?nc gosub SHRB              ; bits 56-63
        rcr 11
        reg=c 7
        c=0     w
        sel ram                     ; "c=reg 0" reads the selected register
        c=reg 0
        ?nc gosub SHRW              ; bits 0-55
        reg=c 0
        rtn

; ADDVX: X = X + V + s6 over 128 bits, V = Z (s10) or LSTx, complemented
; (all 128 bits) with s13; s6 = carry out. So X - V is s13 = s6 = 1, no
; borrow = s6. Uses A, B, C. RAM select ends 0x010.
ADDVX:
        c=0     w
        sel ram
        ? s=1   10
        ?c go   AV_Z0
        c=reg 3
        ?nc go  AV_0
AV_Z0:
        c=reg 1
AV_0:
        ?nc gosub AV_CPL
        a=c     w
        c=n
        ?nc gosub ADDW
        n=c
        m=c
        c=reg 7
        ? s=1   10
        ?c go   AV_Z1
        rcr 9
        ?nc go  AV_1
AV_Z1:
        rcr 5
AV_1:
        ?nc gosub AV_CPL
        a=0     w
        pt= 1
        a=c     wp
        c=reg 7
        ?nc gosub ADDB
        reg=c 7
        ?nc gosub SEL10
        ? s=1   10
        ?c go   AV_Z2
        c=reg 5
        ?nc go  AV_2
AV_Z2:
        c=reg 3
AV_2:
        ?nc gosub AV_CPL
        a=c     w
        c=reg 1
        ?nc gosub ADDW
        reg=c 1
        c=reg 6
        ? s=1   10
        ?c go   AV_Z3
        rcr 8
        ?nc go  AV_3
AV_Z3:
        rcr 4
AV_3:
        ?nc gosub AV_CPL
        a=0     w
        pt= 1
        a=c     wp
        c=reg 6
        ?nc gosub ADDB
        reg=c 6
        rtn
AV_CPL:
        ? s=1   13
        ?nc rtn
        c=-c-1  w
        rtn

; DECN: CTRL count (digits 5-3): C = 0 if it is 0, else count - 1 is
; stored and C <> 0. Uses A, C. RAM select ends 0x010.
DECN:
        ?nc gosub GETN
        ? a#0   x
        ?c go   DE_1
        c=0     w
        rtn
DE_1:
        a=a-1   x
        ac ex   x
        rcr 11
        reg=c 0
        c=0     w
        c=c+1   w
        rtn

; WCNT: CTRL count = W. ACNT: CTRL count = 128 - W.
WCNT:
        ?nc gosub KW
        c=0     w
        ldi $040
        c=a+c   x
        ?nc go  PUTN
ACNT:
        ?nc gosub KW
        c=0     w
        ldi $040
        ac ex   w
        c=a-c   x
        ?nc go  PUTN

; YSHLN, YSHRN: Y shifted left / right by the CTRL count.
YSHLN:
        ?nc gosub DECN
        ? c#0   w
        ?nc rtn
        a=0     x
        ?nc gosub SHL1YC
        ?nc go  YSHLN
YSHRN:
        ?nc gosub DECN
        ? c#0   w
        ?nc rtn
        s=0     6
        ?nc gosub SHR1Y
        ?nc go  YSHRN

; MSTEP: CTRL count steps of: if Y bit 0 is set, clear it and X = X +
; V (s6 = carry); then (carry : X : Y) >>= 1. With X = 0 and W steps:
; X:Y = Y * V << (128 - W) (shift and add). It also undoes DIVN step by
; step (the quotient bits say where V was subtracted).
MSTEP:
        ?nc gosub DECN
        ? c#0   w
        ?nc rtn
        s=0     6
        c=0     w
        sel ram
        c=reg 0
        a=0     w
        a=a+1   w
        c=c&a
        ? c#0   w
        ?nc go  MS_SH
        c=reg 0
        c=c-1   w
        reg=c 0
        s=0     13
        ?nc gosub ADDVX
MS_SH:
        ?nc gosub SHR1X
        ?nc gosub SHR1Y
        ?nc go  MSTEP

; DIVN: CTRL count steps of restoring division: (X : Y) <<= 1; if X >=
; V (or a bit went out of X, W = 128): X = X - V, Y bit 0 = 1.
DIVN:
        ?nc gosub DECN
        ? c#0   w
        ?nc rtn
        a=0     x
        ?nc gosub SHL1YC            ; A.x = Y bit 127
        ?nc gosub SHL1XC            ; A.x = X bit 127 (R bit 128)
        s=0     3
        ? a#0   x
        ?nc go  DN_1
        s=1     3
DN_1:
        s=1     13
        s=1     6
        ?nc gosub ADDVX             ; X - V, s6 = no borrow
        ? s=1   3
        ?c go   DN_Q
        ? s=1   6
        ?c go   DN_Q
        s=0     13
        s=0     6
        ?nc gosub ADDVX             ; borrow: X + V back
        ?nc go  DIVN
DN_Q:
        c=0     w
        sel ram
        c=reg 0
        c=c+1   w                   ; quotient bit
        reg=c 0
        ?nc go  DIVN

; NEG2W: (X : Y) = -(X : Y) over 2W bits (X high), 1's: not. Top level.
NEG2W:
        ?nc gosub SWAPXY            ; X = low
        ? s=1   7
        ?c go   N2_1
        ?nc gosub BITLEN
        ? a#0   x
        ?c go   N2_2
        ?nc gosub SWAPXY            ; low 0: (-high) : 0
        ?nc go  NEG2X
N2_2:
        ?nc gosub NEG2X             ; -low, high = not high
        ?nc gosub SWAPXY
        ?nc go  NOTW
N2_1:
        ?nc gosub NOTW
        ?nc gosub SWAPXY
        ?nc go  NOTW

; RL_DROP, RL_EXIT: LSTx = X before the function (from V = |X| in LSTx
; and the sign), then FN_EXIT_DROP or FN_EXIT.
RL_DROP:
        s=1     3
        ?nc go  RL_0
RL_EXIT:
        s=0     3
RL_0:
        ?nc gosub R4
        pt= 0
        ? c#0   p
        ?nc go  RL_1
        ?nc gosub SWAPXL
        ?nc gosub NEGX
        ?nc gosub SWAPXL
RL_1:
        ? s=1   3
        ?c golong FN_EXIT_DROP
        ?nc golong FN_EXIT

; ------------------------------------------------------ * and DBLX
; *: X <- Y * X (drop); G = result beyond the range, the low W bits with
; the sign bit of the true result then (Model16C.mul). DBLX: Y <- low,
; X <- high W bits of the 2W-bit product (Z, T kept), G cleared.
F_MUL:
        c=0     w
        sel ram
        c=0     w
        reg=c 4
        ?nc go  FM_0
F_DBLX:
        c=0     w
        sel ram
        c=0     w
        pt= 2
        lc 1
        reg=c 4
FM_0:
        ?nc gosub SIGNS
        ?nc gosub NEGXS             ; X = |X|
        ?nc gosub SWAPXL            ; LSTx = V = |X|
        ?nc gosub R4
        pt= 1
        ? c#0   p
        ?nc go  FM_1
        ?nc gosub SWAPXY            ; Y = |Y|
        ?nc gosub NEGX
        ?nc gosub SWAPXY
FM_1:
        ?nc gosub CLRX
        ?nc gosub WCNT
        s=0     10
        ?nc gosub MSTEP             ; X:Y = P << (128 - W)
        ?nc gosub ACNT
        ?nc gosub YSHRN             ; X = P >> W, Y = P mod 2^W
        ?nc gosub R4
        pt= 2
        ? c#0   p
        ?c go   FX_DBLX
        s=0     13                  ; *: out of range if the high part
        ?nc gosub BITLEN            ;  is not 0 ...
        ? a#0   x
        ?nc go  FX_1
        s=1     13
FX_1:
        ?nc gosub COPYYX            ; X = P mod 2^W
        ? s=1   8
        ?c go   FX_G                ; unsigned: that is all
        ?nc gosub TOPBIT            ; ... or the low part reaches 2^(W-1)
        ? c#0   x
        ?nc go  FX_2
        ? s=1   7
        ?c go   FX_O                ; 1's: out
        ?nc gosub R4
        pt= 3
        ? c#0   p
        ?nc go  FX_O                ; 2's positive: out
        ?nc gosub CLRTOP            ; 2's negative: in range only for
        ?nc gosub BITLEN            ;  exactly 2^(W-1)
        s=0     6
        ? a#0   x
        ?nc go  FX_3
        s=1     6
FX_3:
        ?nc gosub SETTOP
        ? s=1   6
        ?nc go  FX_2
FX_O:
        s=1     13
FX_2:
        ?nc gosub R4
        pt= 3
        ? c#0   p
        ?nc go  FX_4
        ?nc gosub NEGX              ; negative result
FX_4:
        ? s=1   13                  ; out of range: sign bit of the
        ?nc go  FX_G                ;  true result
        ?nc gosub CLRTOP
        ?nc gosub R4
        pt= 3
        ? c#0   p
        ?nc go  FX_G
        ?nc gosub SETTOP
FX_G:
        c=0     w
        sel ram
        ?nc gosub FLAG_G
        ?nc golong RL_DROP
FX_DBLX:
        ?nc gosub R4
        pt= 3
        ? c#0   p
        ?nc go  FX_D1
        ?nc gosub NEG2W             ; negative product over 2W bits
FX_D1:
        s=0     13
        c=0     w
        sel ram
        ?nc gosub FLAG_G
        ?nc golong RL_EXIT

; ------------------------------------------------------ / and RMD
; /: X <- Y / X (drop), carry = remainder <> 0, G for -2^(W-1) / -1 (2's;
; result 0). RMD: X <- remainder with the sign of Y, flags unchanged.
; Error 0 for X = 0 (or -0).
F_DIV:
        ?nc gosub ZDIVCHK
        c=0     w
        sel ram
        c=0     w
        reg=c 4
        ?nc go  FD_0
F_RMD:
        ?nc gosub ZDIVCHK
        c=0     w
        sel ram
        c=0     w
        pt= 2
        lc 1
        reg=c 4
FD_0:
        ?nc gosub SIGNS
        ?nc gosub NEGXS
        ?nc gosub SWAPXL            ; LSTx = V = |X|
        ?nc gosub R4
        pt= 1
        ? c#0   p
        ?nc go  FD_1
        ?nc gosub SWAPXY            ; Y = |Y|
        ?nc gosub NEGX
        ?nc gosub SWAPXY
FD_1:
        ?nc gosub CLRX
        ?nc gosub ACNT
        ?nc gosub YSHLN             ; dividend to the top of Y
        ?nc gosub WCNT
        s=0     10
        ?nc gosub DIVN              ; Y = quotient, X = remainder
        ?nc gosub R4
        pt= 2
        ? c#0   p
        ?c go   FD_R
        s=0     10                  ; /: carry = remainder <> 0
        ?nc gosub BITLEN
        ? a#0   x
        ?nc go  FD_2
        s=1     10
FD_2:
        ?nc gosub COPYYX
        s=0     13
        ? s=1   8
        ?c go   FD_F
        ?nc gosub R4
        pt= 3
        ? c#0   p
        ?nc go  FD_3
        ?nc gosub NEGX
        ?nc go  FD_F
FD_3:
        ?nc gosub TOPBIT            ; positive 2^(W-1): only -2^(W-1) / -1
        ? c#0   x
        ?nc go  FD_F
        s=1     13
        ?nc gosub CLRX
FD_F:
        c=0     w
        sel ram
        ?nc gosub FLAGS_CG
        ?nc golong RL_DROP
FD_R:
        ?nc gosub R4                ; RMD: the sign of Y
        pt= 1
        ? c#0   p
        ?nc go  FD_R1
        ?nc gosub NEGX
FD_R1:
        ?nc golong RL_DROP

; ------------------------------------------------------ DBL/ and DBLR
; Dividend Y:Z (2W bits, Y high), divisor X. DBL/: X <- quotient, carry =
; remainder <> 0, G cleared; Error 0 if the quotient does not fit W bits
; in the complement mode. DBLR: X <- remainder with the sign of the
; dividend, flags unchanged; Error 0 if the quotient reaches 2^W (that is
; 2^max(64, W)). Both: Error 0 for X = 0; Y <- T, Z <- T. V = |X| in Z.
F_DBLDIV:
        ?nc gosub ZDIVCHK
        c=0     w
        sel ram
        c=0     w
        reg=c 4
        ?nc go  FB_0
F_DBLR:
        ?nc gosub ZDIVCHK
        c=0     w
        sel ram
        c=0     w
        pt= 2
        lc 1
        reg=c 4
FB_0:
        ?nc gosub SIGNS             ; digit 1 = sign of the dividend
        ?nc gosub SWAPXZ            ; X = low, Z = X
        ?nc gosub SWAPXY            ; X = high, Y = low
        ?nc gosub R4
        pt= 1
        ? c#0   p
        ?nc go  FB_1
        ?nc gosub NEG2W             ; |dividend|
FB_1:
        ?nc gosub SWAPXZ            ; X = divisor, Z = high
        ?nc gosub NEGXS
        ?nc gosub SWAPXZ            ; X = high, Z = V = |divisor|
        s=1     10
        s=1     13
        s=1     6
        ?nc gosub ADDVX             ; high - V: no borrow = quotient
        s=0     3                   ;  >= 2^W
        ? s=1   6
        ?nc go  FB_2
        s=1     3
FB_2:
        s=0     13
        s=0     6
        ?nc gosub ADDVX             ; high back
        ? s=1   3
        ?c go   FB_UNDO
        ?nc gosub ACNT
        ?nc gosub YSHLN             ; low part to the top of Y
        ?nc gosub WCNT
        ?nc gosub DIVN              ; Y = quotient, X = remainder
        ?nc gosub BITLEN
        ? a#0   x
        ?nc go  FB_3
        ?nc gosub R4
        pt= 4
        lc 1
        reg=c 4                     ; remainder <> 0
FB_3:
        ?nc gosub R4
        pt= 2
        ? c#0   p
        ?c go   FB_R
        ?nc gosub SWAPXY            ; DBL/: X = quotient, Y = remainder
        ? s=1   8
        ?c go   FB_OK
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc go  FB_OK
        ? s=1   7
        ?c go   FB_OVF              ; 1's: does not fit
        ?nc gosub R4
        pt= 3
        ? c#0   p
        ?nc go  FB_OVF              ; positive: does not fit
        ?nc gosub CLRTOP            ; negative: fits only as -2^(W-1)
        ?nc gosub BITLEN
        s=0     3
        ? a#0   x
        ?nc go  FB_4
        s=1     3
FB_4:
        ?nc gosub SETTOP
        ? s=1   3
        ?c go   FB_OVF
FB_OK:
        ?nc gosub R4
        pt= 3
        ? c#0   p
        ?nc go  FB_5
        ?nc gosub NEGX
FB_5:
        s=0     10
        ?nc gosub R4
        pt= 4
        ? c#0   p
        ?nc go  FB_6
        s=1     10
FB_6:
        s=0     13
        c=0     w
        sel ram
        ?nc gosub FLAGS_CG
        ?nc go  FB_END
FB_R:
        ?nc gosub R4                ; DBLR: the sign of the dividend
        pt= 1
        ? c#0   p
        ?nc go  FB_END
        ?nc gosub NEGX
FB_END:
        ?nc gosub SWAPXZ            ; X = V, Z = result
        ?nc gosub NEGXS             ; X = the divisor as it was
        ?nc gosub SWAPXL            ; LSTx = divisor
        ?nc gosub SWAPXZ            ; X = result, Z = old LSTx
        ?nc gosub DROP1             ; Y <- Z, Z <- T
        ?nc golong FN_EXIT_DROP     ; Y <- T, Z <- T
FB_OVF:
        ?nc gosub SWAPXY            ; X = remainder, Y = quotient
        ?nc gosub WCNT
        s=1     10
        ?nc gosub MSTEP             ; undo the division
        ?nc gosub ACNT
        ?nc gosub YSHRN             ; X = high, Y = low again
FB_UNDO:
        ?nc gosub SWAPXZ            ; X = V, Z = high
        ?nc gosub NEGXS
        ?nc gosub SWAPXZ            ; X = high, Z = divisor
        ?nc gosub R4
        pt= 1
        ? c#0   p
        ?nc go  FB_U1
        ?nc gosub NEG2W
FB_U1:
        ?nc gosub SWAPXY            ; X = low, Y = high
        ?nc gosub SWAPXZ            ; X = divisor, Z = low
        ?nc golong ERROR0

; ================================================ stage 5: registers, I
; I in plus mode has max(68, W) bits. Bits 0-67 stay where the original
; keeps them (0xFD, 0xFE digits 5-3), so original code that only reads I
; (GTO I, GSB I, RCL (i), STO (i) with a small |I|) works unchanged;
; bits 64-119 are in RAM 0x17 and 120-127 in 0x16 digits 11-10 (the
; stack's 56/8 split), 0xFE digit 5 mirrors 0x17 digit 0 (SYNCI).
; Storage registers: the original nibble array from RAM 0xE0 digit 0 up,
; register n = nibbles n k .. n k + k - 1, k = ceil(W/4), now up to 32.

; KI: A.x = max(4, W - 64) (the width of I - 64). Uses C. RAM 0x010.
KI:
        ?nc gosub KW
        c=0     w
        ldi $004
        ? a<c   x
        ?nc rtn
        a=0     w
        a=c     x
        rtn

; TRUNCI: X = X mod 2^max(68, W). NOTI: X = not X within that width.
; NEGXI: X = -X there (1's: not). TOPBITI: C.x = bit max(68, W) - 1.
TRUNCI:
        ?nc gosub KI
        ?nc go  TRK
NOTI:
        ?nc gosub NOT128
        ?nc go  TRUNCI
NEGXI:
        ? s=1   7
        ?c go   NOTI
        ?nc gosub NOT128
        ?nc gosub INCX
        ?nc go  TRUNCI
TOPBITI:
        ?nc gosub KI
        a=a-1   x
        ?nc go  XBIT

; SYNCI: 0xFE digit 5 (I bits 64-67 for the original) = RAM 0x17 digit
; 0. Uses A, C.
SYNCI:
        ?nc gosub SEL10
        c=reg 7
        rcr 9                       ; digit 0 to digit 5
        a=c     w
        c=0     w
        ldi $0fe
        sel ram
        c=reg 14
        pt= 5
        ac ex   p
        reg=c 14
        rtn

; IWSIZE: WSIZE into or within plus mode: I bits 64-67 from the original
; place (they may have changed while plus mode was off), then ITRUNC: I
; cut to max(68, W) bits. Uses A, B, C.
IWSIZE:
        c=0     w
        ldi $0fe
        sel ram
        c=reg 14
        rcr 5                       ; digit 5 to digit 0
        a=c     w
        ?nc gosub SEL10
        c=reg 7
        pt= 0
        ac ex   p
        reg=c 7
ITRUNC:
        ?nc gosub KI
        ?nc gosub MASK64            ; C, B = 2^(width - 64) - 1
        a=c     w
        c=reg 7
        c=c&a
        reg=c 7
        c=0     w
        c=c-1   w
        pt= 1
        c=b     wp
        rcr 4                       ; the byte mask to digits 11-10
        a=c     w
        c=reg 6
        c=c&a
        reg=c 6
        ?nc go  SYNCI

; SWAPXI: X <-> I, all 128 bits. COPYIX: X <- I (s6: I is not written).
; Use A, B, C, G, M, s6.
COPYIX:
        s=1     6
        ?nc go  SI_0
SWAPXI:
        s=0     6
SI_0:
        c=0     w
        ldi $0fe
        sel ram
        c=reg 13                    ; I bits 0-55 (0xFD)
        c<>n
        ? s=1   6
        ?c go   SI_1
        reg=c 13
SI_1:
        c=n
        m=c
        c=0     w
        sel ram
        c=reg 7
        pt= 0
        g=c                         ; X bits 56-63
        c=0     w
        ldi $0fe
        sel ram
        c=reg 14
        pt= 3
        c<>g                        ; 0xFE digits 4-3 <-> G
        ? s=1   6
        ?c go   SI_2
        reg=c 14
SI_2:
        c=0     w
        sel ram
        c=reg 7
        pt= 0
        c=g
        reg=c 7
        ?nc gosub SEL10
        c=reg 7
        bc ex   w
        c=reg 1
        ? s=1   6
        ?c go   SI_3
        reg=c 7
SI_3:
        c=b     w
        reg=c 1                     ; bits 64-119
        c=reg 6
        pt= 0
        g=c
        pt= 10
        ? s=1   6
        ?c go   SI_4
        c<>g
        ?nc go  SI_5
SI_4:
        g=c                         ; copy: I's byte stays
SI_5:
        pt= 0
        c=g
        reg=c 6                     ; bits 120-127
        ?nc go  SYNCI

; ISIGN: if s3 (the value just stored in I was negative) and W < 68:
; I bits W..67 = 1, the sign extension to 68 bits. Uses A, B, C.
ISIGN:
        ? s=1   3
        ?nc rtn
        ?nc gosub KW
        c=0     w
        ldi $004
        ? a<c   x
        ?nc rtn
        ?nc gosub MASK64            ; C = 2^k - 1, k = W - 64 = 1..3
        c=-c-1  w
        a=0     w
        pt= 0
        a=c     p                   ; digit 0: bits k..3
        c=0     w
        ldi $010
        sel ram
        c=reg 7
        c=c|a
        reg=c 7
        ?nc go  SYNCI

; SIGNX3: s3 = X negative (1's or 2's complement, bit W - 1).
SIGNX3:
        s=0     3
        ? s=1   8
        ?c rtn
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc rtn
        s=1     3
        rtn

; LIFTX: the stack lift for a new X (T <- Z, Z <- Y, Y <- X), unless
; disabled (s9). Uses A, B, C, G, M.
LIFTX:
        ? s=1   9
        ?c rtn
        ?nc gosub SEL10
        ?nc gosub EXT_LIFT
        c=0     w
        sel ram
        c=n
        ?nc golong STACK_LIFT

; IDXN: CTRL count = |I| (max(68, W) bits, in the complement mode) if
; below 2^12, else 0x3FF (no such register or label). I and X unchanged.
; Top level.
IDXN:
        ?nc gosub SWAPXI            ; X = I
        s=0     3
        ? s=1   8
        ?c go   IX_1
        ?nc gosub TOPBITI
        ? c#0   x
        ?nc go  IX_1
        ?nc gosub NEGXI
        s=1     3
IX_1:
        ?nc gosub BITLEN
        c=0     w
        ldi $00d
        ? a<c   x
        ?c go   IX_2
        c=0     w
        ldi $3ff
        ?nc go  IX_3
IX_2:
        c=n
IX_3:
        ?nc gosub PUTN
        ? s=1   3
        ?nc go  IX_4
        ?nc gosub NEGXI
IX_4:
        ?nc go  SWAPXI

; IDXBIG: after IDXN: C <> 0 if |I| is 0x3FF or more. A.x = |I|.
IDXBIG:
        ?nc gosub GETN
        c=0     w
        ldi $3ff
        ? a<c   x
        ?c go   IB_0
        c=c+1   w
        rtn
IB_0:
        c=0     w
        rtn

; REGSET: register n = A.x. Error 3 unless it ends below the program
; (lowest program register: 0xFE digits 1-0, 0 = none: 0xFD). Then RAM 4
; = pool pointer: digits 2-0 register, digit 3 digit, digits 5-4 nibbles
; left (k). Uses A, B, C, M; CTRL digits 5-3.
REGSET:
        c=0     w
        ldi $020
        ? a<c   x                   ; n < 32 (k >= 17: at most 23 fit)
        ?nc go  ERROR3
        ac ex   w
        ?nc gosub PUTN              ; n
        c=0     w
        ldi $0fe
        sel ram
        c=reg 14
        a=0     w
        pt= 1
        a=c     wp                  ; A.x = lowest program register
        ? a#0   x
        ?c go   RS_1
        c=0     w
        ldi $0fd
        a=c     x
RS_1:
        c=0     w
        ldi $0e0
        a=a-c   x                   ; P = registers below the program
        c=0     w
        c=a+c   x
        a sl    x                   ; 16 P
        c=c+c   x
        a=a-c   x                   ; 14 P nibbles
        c=0     w
        sel ram
        ac ex   w
        reg=c 4                     ; (RAM 4 = pool size for now)
        ?nc gosub KW
        c=0     w
        ldi $043
        a=a+c   x                   ; W + 3
        c=0     w
        ldi $004
        ?nc gosub DIVQ              ; A.x = k
        b=a     w
        ?nc gosub GETN              ; A.x = n
        ac ex   w                   ; C.x = n (counter)
        a=0     w
RS_2:
        ? c#0   x
        ?nc go  RS_3
        a=a+b   x
        c=c-1   x
        ?nc go  RS_2
RS_3:
        c=0     w
        c=a+c   x
        m=c                         ; M = s = n k
        a=a+b   x                   ; end = s + k
        c=0     w
        sel ram
        c=reg 4
        ac ex   x
        ? a<c   x                   ; pool < end ?
        ?c go   ERROR3
        c=m
        a=0     w
        a=c     x
        c=0     w
        ldi $00e
        ?nc gosub DIVQ              ; A.x = s div 14, C.x = s mod 14
        rcr 11                      ; digit to digit 3
        ac ex   w
        a=a+c   x
        c=0     w
        ldi $0e0
        a=a+c   x                   ; register
        c=b     w
        rcr 10                      ; k to digits 5-4
        a=a+c   w
        c=0     w
        sel ram
        ac ex   w
        reg=c 4
        rtn

; REGIO: 32 steps over X's nibbles from the bottom (SHR4X). For the
; first k of them also the pool nibble at the RAM 4 pointer: s13 = X's
; nibble is written there, s3 = the pool nibble goes into X (from the
; top). Otherwise X's own nibble goes back in (rotation), or 0 when
; reading. After 32 steps X = the register (s3) or unchanged. Uses A, B,
; C, G, M.
REGIO:
        c=0     w
        ldi $020
        ?nc gosub PUTN
RI_L:
        ?nc gosub DECN
        ? c#0   w
        ?nc rtn
        c=n
        a=0     w
        pt= 0
        a=c     p                   ; X bits 0-3
        c=0     w
        c=a+c   w
        rcr 13
        c=a+c   w
        bc ex   w                   ; B digits 1-0 = that nibble
        c=0     w
        sel ram
        c=reg 4
        rcr 4
        a=0     w
        pt= 1
        a=c     wp                  ; nibbles left in the register
        ? a#0   x
        ?nc go  RI_P
        ?nc gosub NXCH
        ?nc go  RI_S
RI_P:
        ? s=1   3                   ; reading past k: 0
        ?nc go  RI_S
        c=b     w
        pt= 1
        c=0     p
        bc ex   w
RI_S:
        ?nc gosub SHR4X
        ?nc go  RI_L

; NXCH: the pool nibble at the RAM 4 pointer: s3: B digit 1 = it; s13:
; it = B digit 0. Pointer to the next nibble, nibbles left - 1. Uses A,
; C, M.
NXCH:
        c=0     w
        sel ram
        c=reg 4
        rcr 3
        a=0     w
        pt= 0
        a=c     p                   ; A.x = digit d
        rcr 11
        sel ram                     ; the pool register
        c=reg 0                     ; (c=data)
NX_R:
        ? a#0   x
        ?nc go  NX_1
        rcr 1
        a=a-1   x
        ?nc go  NX_R
NX_1:
        ? s=1   3
        ?nc go  NX_2
        rcr 13
        pt= 1
        bc ex   p                   ; B digit 1 = the pool nibble
        c=b     p
        rcr 1
NX_2:
        ? s=1   13
        ?nc go  NX_3
        pt= 0
        c=b     p                   ; pool nibble = B digit 0
NX_3:
        m=c
        c=0     w
        sel ram
        c=reg 4
        rcr 3
        a=0     w
        pt= 0
        a=c     p                   ; d
        c=0     w
        ldi $00e
        ac ex   w
        a=a-c   x                   ; 14 - d
        c=0     w
        sel ram
        c=reg 4
        sel ram                     ; the pool register again
        c=m
NX_B:
        ? a#0   x
        ?nc go  NX_4
        rcr 1
        a=a-1   x
        ?nc go  NX_B
NX_4:
        data=c                      ; (not reg=c 0: that is register 0)
        c=0     w
        sel ram
        c=reg 4
        pt= 3
        c=c+1   p                   ; next digit
        a=c     w
        c=0     w
        pt= 3
        lc e
        pt= 3
        ? a#c   p
        ?c go   NX_5
        ac ex   w
        c=0     p                   ; past digit 13: next register
        c=c+1   x
        ?nc go  NX_6
NX_5:
        ac ex   w
NX_6:
        rcr 4
        c=c-1   x                   ; one nibble less
        rcr 10
        reg=c 4
        rtn

; P_RCL: RCL n (hook 0x08FA; also RCL (i) via the original). A.x = n.
P_RCL:
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        ? c#0   p
        ?c go   PR_1
        c=0     w
        sel ram
        ?nc gosub CLR_ENTRY
        ?nc golong RCL_CONT
PR_1:
        c=0     w
        sel ram
        ?nc gosub CLR_ENTRY         ; keeps A
        s=1     3
        s=0     13
        ?nc gosub REGSET            ; Error 3 before any change
        ?nc gosub LIFTX
        ?nc gosub CLRX
        ?nc gosub REGIO
        ?nc gosub TRUNCX
        ?nc golong FN_EXIT

; P_STO: STO n (hook 0x0914; also STO (i) via the original). A.x = n.
P_STO:
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        ? c#0   p
        ?c go   PS_1
        c=0     w
        sel ram
        ?nc gosub REG_WRITE
        ?nc golong STO_CONT
PS_1:
        s=0     3
        s=1     13
        ?nc gosub REGSET
        ?nc gosub REGIO
        c=0     w
        sel ram
        ?nc golong MAIN_LIFT

; ------------------------------------------------------ I functions
; STO I: I <- X, sign-extended to 68 bits if W < 68 (1's, 2's). RCL I: X
; <- I mod 2^W, stack lift. X<>I: both. LSTx unchanged.
F_STOI:
        ?nc gosub SIGNX3
        ?nc gosub SWAPXI
        ?nc gosub COPYIX
        ?nc gosub ISIGN
        ?nc golong FN_EXIT
F_RCLI:
        ?nc gosub LIFTX
        ?nc gosub COPYIX
        ?nc gosub TRUNCX
        ?nc golong FN_EXIT
F_XEXI:
        ?nc gosub SIGNX3
        ?nc gosub SWAPXI
        ?nc gosub TRUNCX
        ?nc gosub ISIGN
        ?nc golong FN_EXIT

; (i), GTO I, GSB I: register or label |I|. With |I| below 2^12 the
; original routines see the same number in the original 68 bits of I;
; RCL (i) and STO (i) then come back through P_RCL / P_STO. X<>(i) is
; new code (the original exchanges 64 bits only).
F_RCLII:
        ?nc gosub IDXN
        ?nc gosub IDXBIG
        ? c#0   w
        ?c go   ERROR3
        c=0     w
        sel ram
        c=reg 4
        ?nc golong $0fab
F_STOII:
        ?nc gosub IDXN
        ?nc gosub IDXBIG
        ? c#0   w
        ?c go   ERROR3
        c=0     w
        sel ram
        c=reg 4
        ?nc golong $0fb1
F_GTOI:
        ?nc gosub IDXN
        ?nc gosub IDXBIG
        ? c#0   w
        ?c go   ERROR4
        c=0     w
        sel ram
        c=reg 4
        ?nc golong $0f8d
F_GSBI:
        ?nc gosub IDXN
        ?nc gosub IDXBIG
        ? c#0   w
        ?c go   ERROR4
        c=0     w
        sel ram
        c=reg 4
        ?nc golong $0f8e
F_XEXII:
        ?nc gosub IDXN
        ?nc gosub GETN              ; A.x = n
        ?nc gosub REGSET            ; Error 3 if not in memory
        s=1     3
        s=1     13
        ?nc gosub REGIO
        ?nc gosub TRUNCX
        ?nc golong FN_EXIT

; CLRREG: clear I's extension, then the original (registers and I).
F_CLRREG:
        ?nc gosub SEL10
        c=0     w
        reg=c 7
        c=reg 6
        pt= 11
        c=0     p
        pt= 10
        c=0     p
        reg=c 6
        c=0     w
        sel ram
        c=reg 4
        ?nc golong $0c0a

; DSZ, ISZ: I -/+ 1 in max(68, W) bits (1's: end-around); the next
; program step is skipped if I is then 0 (or -0).
F_ISZ:
        s=1     3
        ?nc go  FZ_0
F_DSZ:
        s=0     3
FZ_0:
        ?nc gosub SWAPXI            ; X = I
        ? s=1   3
        ?nc go  FZ_D
        ?nc gosub INCX
        ?nc gosub TRUNCI
        ? s=1   7
        ?nc go  FZ_T
        ?nc gosub BITLEN            ; 1's: -0 + 1 = 1
        ? a#0   x
        ?c go   FZ_T
        c=0     w
        c=c+1   w
        n=c
        m=c
        ?nc go  FZ_T
FZ_D:
        ?nc gosub DECX
        ?nc gosub TRUNCI
        ? s=1   7
        ?nc go  FZ_T
        ?nc gosub ONESI             ; 1's: 0 - 1 = -1, not -0
        ? s=1   6
        ?nc go  FZ_T
        ?nc gosub DECX
        ?nc gosub TRUNCI
FZ_T:
        s=0     13                  ; skip if 0, or -0 in 1's
        ?nc gosub BITLEN
        ? a#0   x
        ?c go   FZ_1
        s=1     13
FZ_1:
        ? s=1   7
        ?nc go  FZ_2
        ?nc gosub ONESI
        ? s=1   6
        ?nc go  FZ_2
        s=1     13
FZ_2:
        ?nc gosub SWAPXI
        ? s=1   13
        ?nc go  FZ_3
        c=0     w
        sel ram
        ?nc gosub SKIP
FZ_3:
        c=0     w
        sel ram
        ?nc golong MAIN_LIFT

; ONESI: s6 = X is all ones within max(68, W) bits. X unchanged.
ONESI:
        ?nc gosub NOTI
        s=0     6
        ?nc gosub BITLEN
        ? a#0   x
        ?c go   ON_1
        s=1     6
ON_1:
        ?nc go  NOTI

; ------------------------------------------------------ conditionals
; x=y x#y x>y x<=y x<0 x>0 x=0 x#0 B?: false skips the next program step
; (SKIP, as the original), then the original's ending (0x0F79: X back
; into C, A.x, main loop at 0x008B). Values compare in the complement
; mode; -0 equals 0. Nothing changes (RAM 4 is scratch: that exit skips
; the LSTx save).
F_XEQY:
        ?nc gosub CMPYX
        ? s=1   3
        ?nc go  CT_F
        ?nc go  CT_T
F_XNEY:
        ?nc gosub CMPYX
        ? s=1   3
        ?c go   CT_F
        ?nc go  CT_T
F_XGTY:
        ?nc gosub CMPYX
        ? s=1   6
        ?nc go  CT_F
        ?nc go  CT_T
F_XLEY:
        ?nc gosub CMPYX
        ? s=1   6
        ?c go   CT_F
        ?nc go  CT_T
F_XEQ0:
        ?nc gosub XSTAT
        ? s=1   3
        ?nc go  CT_F
        ?nc go  CT_T
F_XNE0:
        ?nc gosub XSTAT
        ? s=1   3
        ?c go   CT_F
        ?nc go  CT_T
F_XLT0:
        ?nc gosub XSTAT
        ? s=1   6
        ?nc go  CT_F
        ?nc go  CT_T
F_XGT0:
        ?nc gosub XSTAT
        ? s=1   3
        ?c go   CT_F
        ? s=1   6
        ?c go   CT_F
        ?nc go  CT_T
F_BQ:
        ?nc gosub KW                ; B?: bit |X| of Y, |X| up to W - 1
        c=0     w
        ldi $03f
        a=a+c   x
        ?nc gosub COUNTX            ; Error 2 above (nothing changed)
        a=c     w                   ; A.x = n
        c=0     w
        ldi $040
        ? a<c   x
        ?c go   BQ_LO
        a=a-c   x
        ?nc gosub POW2              ; bit n - 64: Y bits 64-127
        a=c     w
        ?nc gosub SEL10
        c=reg 2
        c=c&a
        ? c#0   w
        ?c go   CT_T
        c=reg 6
        rcr 2
        ?nc go  BQ_B
BQ_LO:
        ?nc gosub POW2              ; Y bits 0-63
        a=c     w
        c=0     w
        sel ram
        c=reg 0
        c=c&a
        ? c#0   w
        ?c go   CT_T
        c=reg 7
        rcr 3
BQ_B:
        ab ex   w
        c=c&a
        ? c#0   w
        ?c go   CT_T
CT_F:
        c=0     w
        sel ram
        ?nc gosub SKIP
CT_T:
        c=0     w
        sel ram
        ?nc golong COND_END

; ONESW: s6 = X is all ones within W bits: not X has no bit below W.
; X unchanged. Uses A, B, C (nests 2 deep).
ONESW:
        ?nc gosub NOT128
        s=0     6
        c=n
        ? c#0   w
        ?c go   OW_1
        c=0     w
        sel ram
        c=reg 7
        rcr 2
        pt= 11
        c=0     wp                  ; bits 56-63 at digits 13-12
        ? c#0   w
        ?c go   OW_1
        ?nc gosub KW
        ?nc gosub MASK64            ; bits 64..W-1
        a=c     w
        c=reg 1
        c=c&a
        ? c#0   w
        ?c go   OW_1
        c=reg 6
        ab ex   w
        c=c&a
        ? c#0   w
        ?c go   OW_1
        s=1     6
OW_1:
        ?nc go  NOT128

; XSTAT: s3 = X is 0 (or -0 in 1's), s6 = X is negative (not -0).
; Uses A, B, C. Top level.
XSTAT:
        s=0     3
        ?nc gosub BITLEN
        ? a#0   x
        ?c go   XS_1
        s=1     3
XS_0:
        s=0     6
        rtn
XS_1:
        ? s=1   8
        ?c go   XS_0
        ? s=1   7
        ?nc go  XS_2
        ?nc gosub ONESW             ; 1's: -0 ?
        ? s=1   6
        ?nc go  XS_2
        s=1     3
        ?nc go  XS_0
XS_2:
        s=0     6
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc rtn
        s=1     6
        rtn

; CMPYX: s3 = Y equals X, s6 = Y < X, in the complement mode (-0 = 0).
; D = Y - X (W bits) gives both: unsigned Y < X is the borrow; signed it
; is the sign of D xor the overflow (the signs of Y and X differ and D's
; differs from Y's). X is restored as Y - D, -0 put back. RAM 4 digits:
; 0/1 = X/Y was -0 (1's), 2/3 = sign of X/Y, 4 = borrow, 5 = equal, 6 =
; less. Uses A, B, C, G, M. Top level.
CMPYX:
        c=0     w
        sel ram
        c=0     w
        reg=c 4
        ? s=1   7
        ?nc go  CM_1
        ?nc gosub ONESW             ; 1's: -0 counts as 0
        ? s=1   6
        ?nc go  CM_0
        ?nc gosub CLRX
        ?nc gosub R4
        pt= 0
        lc 1
        reg=c 4
CM_0:
        ?nc gosub SWAPXY
        ?nc gosub ONESW
        ? s=1   6
        ?nc go  CM_01
        ?nc gosub CLRX
        ?nc gosub R4
        pt= 1
        lc 1
        reg=c 4
CM_01:
        ?nc gosub SWAPXY
CM_1:
        ? s=1   8
        ?c go   CM_2
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc go  CM_11
        ?nc gosub R4
        pt= 2
        lc 1
        reg=c 4
CM_11:
        ?nc gosub TOPBITY
        ? c#0   x
        ?nc go  CM_2
        ?nc gosub R4
        pt= 3
        lc 1
        reg=c 4
CM_2:
        ?nc gosub NOT128            ; X = Y - X (128 bits)
        s=1     6
        ?nc gosub ADD128
        ? s=1   6
        ?c go   CM_3
        ?nc gosub R4                ; borrow
        pt= 4
        lc 1
        reg=c 4
CM_3:
        ?nc gosub TRUNCX
        ?nc gosub BITLEN
        ? a#0   x
        ?c go   CM_4
        ?nc gosub R4                ; equal
        pt= 5
        lc 1
        reg=c 4
CM_4:
        ? s=1   8
        ?nc go  CM_5
        ?nc gosub R4                ; unsigned: less = borrow
        pt= 4
        ? c#0   p
        ?nc go  CM_8
        ?nc go  CM_L
CM_5:
        s=0     6                   ; signed: s6 = sign of D ...
        ?nc gosub TOPBIT
        ? c#0   x
        ?nc go  CM_6
        s=1     6
CM_6:
        ?nc gosub R4
        rcr 2                       ; digit 0 = sign of X, 1 = sign of Y
        a=c     w
        a sr    w                   ; A digit 0 = sign of Y
        pt= 0
        ? a#c   p
        ?nc go  CM_7                ; same signs: no overflow
        c=0     w
        ? s=1   6
        ?nc go  CM_61
        c=c+1   w                   ; C digit 0 = sign of D
CM_61:
        ? a#c   p
        ?nc go  CM_7                ; D's sign = Y's: no overflow
        ? s=1   6                   ; ... xor the overflow
        ?c go   CM_8
        ?nc go  CM_L
CM_7:
        ? s=1   6
        ?nc go  CM_8
CM_L:
        ?nc gosub R4                ; less
        pt= 6
        lc 1
        reg=c 4
CM_8:
        ?nc gosub NOT128            ; X = Y + not D + 1 = Y - D
        s=1     6
        ?nc gosub ADD128
        ?nc gosub TRUNCX
        ?nc gosub R4
        pt= 0
        ? c#0   p
        ?nc go  CM_9
        ?nc gosub CLRX              ; X was -0
        ?nc gosub NOTW
CM_9:
        ?nc gosub R4
        pt= 1
        ? c#0   p
        ?nc go  CM_10
        ?nc gosub SWAPXY            ; Y was -0
        ?nc gosub CLRX
        ?nc gosub NOTW
        ?nc gosub SWAPXY
CM_10:
        s=0     3
        s=0     6
        ?nc gosub R4
        pt= 5
        ? c#0   p
        ?nc go  CM_12
        s=1     3
CM_12:
        pt= 6
        ? c#0   p
        ?nc rtn
        s=1     6
        rtn

; ------------------------------------------------------ SQRT
; SQRT: X <- floor(sqrt(X)) (Model16C.sqrt): Error 0 for X < 0 (-0 is
; not), carry = not exact, 0 and -0 unchanged. LSTx = X.
; Restoring square root by pairs of bits from the top, without a 66-bit
; trial value: with the pair p, rem' = 4 rem + p >= 4 root + 1 is rem >=
; root (p > 0) or rem >= root + 1 (p = 0); then rem' = 4 (rem - root) +
; p - 1 (that is rem - root - 1 and 3 for p = 0) and root = 2 root + 1,
; else rem' = 4 rem + p and root = 2 root. x is read from LSTx, rem grows
; in X, root (64 bits at most) is in RAM 4 (bits 0-55) and RAM 0x16
; digits 13-12 (bits 56-63).
F_SQRT:
        ?nc gosub XSTAT             ; s3 = 0 or -0, s6 = negative
        ? s=1   6
        ?c go   ERROR0
        ?nc gosub SAVE_LX
        s=0     10
        ? s=1   3
        ?c go   SQ_E                ; 0, -0: unchanged, exact
        ?nc gosub CLRX              ; rem = 0
        c=0     w
        sel ram
        c=0     w
        reg=c 4                     ; root = 0
        ?nc gosub SEL10
        c=reg 6
        pt= 13
        c=0     p
        pt= 12
        c=0     p
        reg=c 6
        ?nc gosub KW
        c=0     w
        ldi $041
        a=a+c   x                   ; W + 1
        c=0     w
        ldi $002
        ?nc gosub DIVQ              ; ceil(W/2) pairs
        ac ex   w
        ?nc gosub PUTN
SQ_L:
        ?nc gosub DECN              ; count = pair index p
        ? c#0   w
        ?nc go  SQ_D
        ?nc gosub GETN
        c=0     w
        c=a+c   x
        a=a+c   x
        a=a+1   x                   ; bit 2p + 1
        ?nc gosub LBIT
        s=0     10
        ? c#0   x
        ?nc go  SQ_1
        s=1     10                  ; s10 = high bit of the pair
SQ_1:
        ?nc gosub GETN
        c=0     w
        c=a+c   x
        a=a+c   x                   ; bit 2p
        ?nc gosub LBIT
        s=0     3
        ? c#0   x
        ?nc go  SQ_2
        s=1     3                   ; s3 = low bit
SQ_2:
        s=0     6                   ; rem - root (p > 0) or rem - root - 1
        ? s=1   10
        ?c go   SQ_3
        ? s=1   3
        ?nc go  SQ_4
SQ_3:
        s=1     6
SQ_4:
        s=1     13
        ?nc gosub ADDR
        ? s=1   6
        ?nc go  SQ_0                ; borrow: root bit 0
        ?nc gosub ROOT2             ; root = 2 root + 1
        a=0     x                   ; insert p - 1 (mod 4): bit 1 = (p1 =
        ? s=1   10                  ;  p0), bit 0 = not p0
        ?c go   SQ_5
        ? s=1   3
        ?c go   SQ_6
        a=a+1   x
        ?nc go  SQ_6
SQ_5:
        ? s=1   3
        ?nc go  SQ_6
        a=a+1   x
SQ_6:
        ?nc gosub SHL1XC
        a=0     x
        ? s=1   3
        ?c go   SQ_7
        a=a+1   x
SQ_7:
        ?nc gosub SHL1XC
        ?nc go  SQ_L
SQ_0:
        s=0     13                  ; rem back: + root (+ 1 for p = 0)
        s=0     6
        ? s=1   10
        ?c go   SQ_8
        ? s=1   3
        ?c go   SQ_8
        s=1     6
SQ_8:
        ?nc gosub ADDR
        s=0     6
        ?nc gosub ROOT2             ; root = 2 root
        a=0     x                   ; insert p
        ? s=1   10
        ?nc go  SQ_9
        a=a+1   x
SQ_9:
        ?nc gosub SHL1XC
        a=0     x
        ? s=1   3
        ?nc go  SQ_10
        a=a+1   x
SQ_10:
        ?nc gosub SHL1XC
        ?nc go  SQ_L
SQ_D:
        ?nc gosub BITLEN            ; carry = remainder <> 0
        s=0     10
        ? a#0   x
        ?nc go  SQ_11
        s=1     10
SQ_11:
        ?nc gosub CLRX              ; X = root
        c=0     w
        sel ram
        c=reg 4
        n=c
        m=c
        ?nc gosub SEL10
        c=reg 6
        rcr 12
        a=c     w                   ; A digits 1-0 = root bits 56-63
        c=reg 6
        pt= 13
        c=0     p
        pt= 12
        c=0     p
        reg=c 6
        c=0     w
        sel ram
        c=reg 7
        pt= 1
        ac ex   wp
        reg=c 7
SQ_E:
        c=0     w
        sel ram
        ?nc gosub FLAG_C
        ?nc golong FN_EXIT

; LBIT: A.x = j (0..127): C.x = bit j of LSTx (1 or 0). Uses A, B, C.
LBIT:
        c=0     w
        ldi $040
        ? a<c   x
        ?c go   LB_LO
        a=a-c   x
        ?nc gosub POW2
        a=c     w
        ?nc gosub SEL10
        c=reg 5
        c=c&a
        ? c#0   w
        ?c go   LB_1
        c=reg 6
        rcr 8                       ; LSTx bits 120-127
        ?nc go  LB_B
LB_LO:
        ?nc gosub POW2
        a=c     w
        c=0     w
        sel ram
        c=reg 3
        c=c&a
        ? c#0   w
        ?c go   LB_1
        c=reg 7
        rcr 9                       ; LSTx bits 56-63
LB_B:
        ab ex   w
        c=c&a
        ? c#0   w
        ?c go   LB_1
        c=0     w
        rtn
LB_1:
        c=0     w
        c=c+1   w
        rtn

; ADDR: X = X + R + s6 over 128 bits, R = the root (RAM 4, RAM 0x16
; digits 13-12), complemented (128 bits) with s13; s6 = carry out. Uses
; A, B, C.
ADDR:
        c=0     w
        sel ram
        c=reg 4
        ?nc gosub AV_CPL
        a=c     w
        c=n
        ?nc gosub ADDW
        n=c
        m=c
        ?nc gosub SEL10
        c=reg 6
        rcr 12
        ?nc gosub AV_CPL
        a=0     w
        pt= 1
        a=c     wp
        c=0     w
        sel ram
        c=reg 7
        ?nc gosub ADDB
        reg=c 7
        c=0     w
        ?nc gosub AV_CPL            ; R bits 64-127 are 0
        a=c     w
        ?nc gosub SEL10
        c=reg 1
        ?nc gosub ADDW
        reg=c 1
        c=0     w
        ?nc gosub AV_CPL
        a=0     w
        pt= 1
        a=c     wp
        c=reg 6
        ?nc gosub ADDB
        reg=c 6
        rtn

; ROOT2: R = 2 R + s6. Uses A, C, G.
ROOT2:
        c=0     w
        sel ram
        c=reg 4
        a=0     x
        c=c+c   w
        ?nc go  R2_1
        a=a+1   x                   ; carry into bit 56
R2_1:
        ? s=1   6
        ?nc go  R2_2
        c=c+1   w
R2_2:
        reg=c 4
        ?nc gosub SEL10
        c=reg 6
        rcr 12
        ?nc gosub DBLB              ; G = 2 (bits 56-63) + carry
        c=reg 6
        pt= 12
        c=g
        reg=c 6
        rtn

; ================================================ stage 6: FLOAT, MEM
; P_FLOAT: FLOAT n from integer mode (hook 0x097F, before the original
; conversion 0x1758, which takes a 64-bit Y). Plus mode: |Y| is shifted
; right s bits until it is below 2^63 (a sticky bit keeps the rounding),
; the sign goes back, X = X + s (W bits, 1's: -0 + 1 = 1), an |X| of
; 2^56 or more becomes +-2^60 (over- or underflow either way), plus mode
; off (FLOAT leaves W = 56), then the original converts Y * 2^X.
P_FLOAT:
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        ? c#0   p
        ?c go   PF_1
        c=0     w
        sel ram
        ?nc golong FLOAT_INT
PF_1:
        s=0     3
        ? s=1   8
        ?c go   PF_2
        ?nc gosub TOPBITY
        ? c#0   x
        ?nc go  PF_2
        s=1     3                   ; Y negative
PF_2:
        ?nc gosub SWAPXY            ; X = Y
        ? s=1   3
        ?nc go  PF_3
        ?nc gosub NEGX              ; |Y|
PF_3:
        s=0     13
        c=0     w
        ?nc gosub PUTN              ; s = 0
PF_L:
        ?nc gosub BITLEN
        c=0     w
        ldi $040
        ? a<c   x                   ; below 2^63 ?
        ?c go   PF_4
        s=0     6
        ?nc gosub SHR1X
        ? s=1   6
        ?nc go  PF_5
        s=1     13                  ; a 1 bit went out
PF_5:
        ?nc gosub GETN
        a=a+1   x
        ac ex   x
        rcr 11
        reg=c 0                     ; s + 1
        ?nc go  PF_L
PF_4:
        ? s=1   13
        ?nc go  PF_6
        a=0     w                   ; sticky bit
        a=a+1   w
        c=n
        c=c|a
        n=c
        m=c
PF_6:
        ? s=1   3
        ?nc go  PF_7
        ?nc gosub NEGX              ; the sign back
PF_7:
        ?nc gosub SWAPXY            ; Y = it, X = the exponent
PF_A:
        ?nc gosub DECN              ; X = X + s
        ? c#0   w
        ?nc go  PF_8
        ?nc gosub INCX
        ?nc gosub TRUNCX
        ? s=1   7
        ?nc go  PF_A
        ?nc gosub BITLEN            ; 1's: -0 + 1 = 1
        ? a#0   x
        ?c go   PF_A
        c=0     w
        c=c+1   w
        n=c
        m=c
        ?nc go  PF_A
PF_8:
        ?nc gosub CXCORE            ; s6 = |X| >= 2^56
        ? s=1   6
        ?nc go  PF_9
        ?nc gosub SIGNX3
        ?nc gosub CLRX
        c=0     w
        sel ram
        c=reg 7
        pt= 1
        lc 1                        ; 2^60
        reg=c 7
        ? s=1   3
        ?nc go  PF_9
        ?nc gosub NEGX              ; -2^60
PF_9:
        ?nc gosub EXT_CLEAR         ; plus mode off
        s=0     6                   ; as 0x0581 left them (s6: the sign of
        s=0     13                  ;  the exponent in 0x1758)
        c=0     w
        sel ram
        ?nc golong FLOAT_INT

; P_MEM: MEM (hook 0x01F4): in plus mode the original's register count
; uses k = ceil(W/4) nibbles instead of the 16 in RAM 6. In: A.x = free
; nibbles, B kept for the original.
P_MEM:
        ac ex   w
        m=c                         ; M = A
        ?nc gosub SEL10
        c=reg 0
        pt= 2
        ? c#0   p
        ?c go   PM_1
        c=m
        ac ex   w
        c=0     w
        sel ram
        ?nc golong MEM_K            ; original: k from RAM 6
PM_1:
        ?nc gosub KW
        c=0     w
        ldi $043
        a=a+c   x                   ; W + 3
        c=0     w
        ldi $004
        ?nc gosub DIVQ              ; A.x = k
        c=m
        ac ex   w                   ; A = free nibbles, C = k
        m=c
        c=0     w
        sel ram
        c=m
        ?nc golong MEM_DIV          ; free nibbles / k

; SEL10: RAM select 0x010 (the extension registers), C = 0x010. A call
; instead of the 4 words c=0 w ; ldi $010 ; sel ram where the return
; stack has a level free (measured, tools/depth16c.py).
SEL10:
        c=0     w
        ldi $010
        sel ram
        rtn
