// The two trampolines and the slot accessor of MonoMod's exception helper (see exhelper.c). Register use follows the
// original: the target is in x9, x0..x7 pass through, and eh_get_exception_ptr preserves x1..x8 so the trampolines can
// call it between setting up arguments and the call.
    .text
    .p2align 2

    .globl eh_get_exception_ptr
    .type eh_get_exception_ptr, %function
eh_get_exception_ptr:
    .cfi_startproc
    stp x7, x8, [sp, #-80]!
    .cfi_def_cfa_offset 80
    stp x5, x6, [sp, #16]
    stp x3, x4, [sp, #32]
    stp x1, x2, [sp, #48]
    stp x29, x30, [sp, #64]
    .cfi_offset x30, -8
    .cfi_offset x29, -16
    add x29, sp, #64
    bl eh_slot
    ldp x29, x30, [sp, #64]
    ldp x1, x2, [sp, #48]
    ldp x3, x4, [sp, #32]
    ldp x5, x6, [sp, #16]
    ldp x7, x8, [sp], #80
    .cfi_def_cfa_offset 0
    .cfi_restore x30
    .cfi_restore x29
    ret
    .cfi_endproc
    .size eh_get_exception_ptr, . - eh_get_exception_ptr

    .globl eh_managed_to_native
    .type eh_managed_to_native, %function
eh_managed_to_native:
    .cfi_startproc
    .cfi_personality 0x9b, DW.ref.eh_personality
    .cfi_lsda 0x1b, .Llsda_managed_to_native
    stp x29, x30, [sp, #-16]!
    .cfi_def_cfa_offset 16
    .cfi_offset x30, -8
    .cfi_offset x29, -16
    mov x29, sp
    blr x9
    .cfi_remember_state
    ldp x29, x30, [sp], #16
    .cfi_def_cfa_offset 0
    .cfi_restore x30
    .cfi_restore x29
    ret
    .cfi_restore_state
.Llanding_managed_to_native:
    // x0 = the _Unwind_Exception that escaped the callee (set by eh_personality); keep it for eh_native_to_managed.
    mov x1, x0
    bl eh_get_exception_ptr
    str x1, [x0]
    mov x0, xzr
    ldp x29, x30, [sp], #16
    .cfi_def_cfa_offset 0
    .cfi_restore x30
    .cfi_restore x29
    ret
    .cfi_endproc
    .size eh_managed_to_native, . - eh_managed_to_native

    .globl eh_native_to_managed
    .type eh_native_to_managed, %function
eh_native_to_managed:
    .cfi_startproc
    stp x20, x19, [sp, #-32]!
    .cfi_def_cfa_offset 32
    .cfi_offset x19, -24
    .cfi_offset x20, -32
    stp x29, x30, [sp, #16]
    .cfi_offset x30, -8
    .cfi_offset x29, -16
    add x29, sp, #16
    mov x19, x9
    mov x20, x0
    bl eh_get_exception_ptr
    str xzr, [x0]
    mov x0, x20
    blr x19
    mov x20, x0
    bl eh_get_exception_ptr
    ldr x0, [x0]
    cbnz x0, .Lreraise
    mov x0, x20
    .cfi_remember_state
    ldp x29, x30, [sp, #16]
    ldp x20, x19, [sp], #32
    .cfi_def_cfa_offset 0
    .cfi_restore x30
    .cfi_restore x29
    .cfi_restore x19
    .cfi_restore x20
    ret
    .cfi_restore_state
.Lreraise:
    bl _Unwind_RaiseException
    brk #1
    .cfi_endproc
    .size eh_native_to_managed, . - eh_native_to_managed

    .section .gcc_except_table,"a",%progbits
    .p2align 2
.Llsda_managed_to_native:
    .long .Llanding_managed_to_native - .Llsda_managed_to_native

    .hidden DW.ref.eh_personality
    .weak DW.ref.eh_personality
    .section .data.DW.ref.eh_personality,"awG",%progbits,DW.ref.eh_personality,comdat
    .p2align 3
    .type DW.ref.eh_personality, %object
    .size DW.ref.eh_personality, 8
DW.ref.eh_personality:
    .xword eh_personality

    .section .note.GNU-stack,"",%progbits
