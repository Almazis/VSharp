namespace VSharp.System

open VSharp
open VSharp.Core

module internal Enum =

    [<Implements("System.Reflection.CorElementType System.Enum.InternalGetCorElementType(this)")>]
    val InternalGetCorElementType : state -> term list -> term

    [<Implements("System.Void System.Enum.GetEnumValuesAndNames(System.Runtime.CompilerServices.QCallTypeHandle, System.Runtime.CompilerServices.ObjectHandleOnStack, System.Runtime.CompilerServices.ObjectHandleOnStack, Interop+BOOL)")>]
    val GetEnumValuesAndNames : state -> term list -> (term * state) list
