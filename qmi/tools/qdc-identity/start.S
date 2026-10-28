/* SPDX-License-Identifier: GPL-3.0-only */
.syntax unified
.arm
.text
.global _start
.type _start,%function
/* Linux ARM EABI entry: r0=loader finalizer, stack={argc, argv, envp,...}.
 * Enter through libc so TLS and the QCCI pthread implementation are initialized.
 */
_start:
    mov fp, #0
    mov lr, #0
    pop {r1}
    mov r2, sp
    push {r2}
    push {r0}
    mov r3, #0
    push {r3}
    ldr r0, =main
    bl __libc_start_main
    udf #0
.size _start, .-_start
.section .note.GNU-stack,"",%progbits
