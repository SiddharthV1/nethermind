// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Int256;
using Org.BouncyCastle.Tls;
using Sigil;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using static Nethermind.Evm.IL.EmitExtensions;

namespace Nethermind.Evm.IL;

public static class StackEmit
{
    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop<T>(this Emit<T> il, Local idx, int count)
    {
        il.LoadLocal(idx);
        il.LoadConstant(count);
        il.Subtract();
        il.StoreLocal(idx);
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop<T>(this Emit<T> il, Local local, Local count)
    {
        il.LoadLocal(local);
        il.LoadLocal(count);
        il.Subtract();
        il.StoreLocal(local);
    }
    /// <summary>
    /// Loads the previous EVM stack value on top of .NET stack.
    /// </summary>
    public static void StackLoadPrevious<T>(this Emit<T> il, Local stackHeadRef, int offset, int count)
    {
        il.LoadLocal(stackHeadRef);

        int offsetFromHead = offset - count;

        if (offsetFromHead == 0) return;

        il.LoadConstant(offsetFromHead * Word.Size);
        il.Convert<nint>();
        il.Call(UnsafeEmit.GetAddBytesOffsetRef<Word>());
    }

    public static void StackSetHead<T>(this Emit<T> il, Local stackHeadRef, int offset)
    {
        if (offset == 0) return;

        il.LoadLocal(stackHeadRef);
        il.LoadConstant(offset * Word.Size);
        il.Convert<nint>();
        il.Call(UnsafeEmit.GetAddBytesOffsetRef<Word>());
        il.StoreLocal(stackHeadRef);
    }

    public static void LoadItemFromSpan<T, U>(this Emit<T> il, Local local, Local idx)
    {
        il.LoadLocalAddress(local);
        il.LoadLocal(idx);
        il.Call(typeof(Span<U>).GetMethod("get_Item"));
    }

    public static void CleanWord<T>(this Emit<T> il, Local stackHeadRef, int offset, int count)
    {
        il.StackLoadPrevious(stackHeadRef, offset, count);
        il.InitializeObject(typeof(Word));
    }


    public static void CleanAndLoadWord<T>(this Emit<T> il, Local stackHeadRef, int offset, int count)
    {
        il.StackLoadPrevious(stackHeadRef, offset, count);
        il.Duplicate();

        il.InitializeObject(typeof(Word));
    }

    /// <summary>
    /// Advances the stack one word up.
    /// </summary>
    public static void StackPush<T>(this Emit<T> il, Local idx, int count)
    {
        il.LoadLocal(idx);
        il.LoadConstant(count);
        il.Add();
        il.StoreLocal(idx);
    }
}

public static class WordEmit
{
    public static void CallGetter<T>(this Emit<T> il, MethodInfo getterInfo, bool isLittleEndian)
    {
        il.Call(getterInfo);
        if (isLittleEndian)
        {
            il.Call(typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), [getterInfo.ReturnType]));
        }
    }

    public static void CallSetter<T>(this Emit<T> il, MethodInfo setterInfo, bool isLittleEndian)
    {
        if (isLittleEndian)
        {
            il.Call(typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), [setterInfo.GetParameters()[0].ParameterType]));
        }
        il.Call(setterInfo);
    }

    public static void EmitIsOneCheck<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        if (BitConverter.IsLittleEndian)
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsOneLittleEndian)).GetMethod;
            il.Call(methodInfo);
        } else
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsOneBigEndian)).GetMethod;
            il.Call(methodInfo);
        }
    }

    public static void EmitIsZeroCheck<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsZero)).GetMethod;
        il.Call(methodInfo);
    }

    public static void EmitIsMinusOneCheck<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsMinusOne)).GetMethod;
        il.Call(methodInfo);
    }

    public static void EmitIsZeroOrOneCheck<T>(this Emit<T> il, Local? word = null)
    {
        if(word is not null)
        {
            il.LoadLocalAddress(word);
        }

        if (BitConverter.IsLittleEndian)
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsOneOrZeroLittleEndian)).GetMethod;
            il.Call(methodInfo);
        }
        else
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsOneOrZeroBigEndian)).GetMethod;
            il.Call(methodInfo);
        }
    }

    public static void EmitIsP255Check<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        if (BitConverter.IsLittleEndian)
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsP255LittleEndian)).GetMethod;
            il.Call(methodInfo);
        }
        else
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsP255BigEndian)).GetMethod;
            il.Call(methodInfo);
        }
    }
}
/// <summary>
/// Extensions for <see cref="ILGenerator"/>.
/// </summary>
static class EmitExtensions
{
    public static class UnsafeEmit {

        public static MethodInfo GetAddOffsetRef<TResult>()
        {
            MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.Add) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
            return method.MakeGenericMethod(typeof(TResult));
        }

        public static MethodInfo GetSubtractOffsetRef<TResult>()
        {
            MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.Subtract) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
            return method.MakeGenericMethod(typeof(TResult));
        }

        public static MethodInfo GetAddBytesOffsetRef<TResult>()
        {
            MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.AddByteOffset) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
            return method.MakeGenericMethod(typeof(TResult));
        }

        public static MethodInfo GetSubtractBytesOffsetRef<TResult>()
        {
            MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.SubtractByteOffset) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
            return method.MakeGenericMethod(typeof(TResult));
        }

        public static MethodInfo GetReadUnalignedMethodInfo<TResult>()
        {
            MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.ReadUnaligned) && m.GetParameters()[0].ParameterType.IsByRef);
            return method.MakeGenericMethod(typeof(TResult));
        }

        public static MethodInfo GetWriteUnalignedMethodInfo<TResult>()
        {
            MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.WriteUnaligned) && m.GetParameters()[0].ParameterType.IsByRef);
            return method.MakeGenericMethod(typeof(TResult));
        }

        public static MethodInfo GetAsMethodInfo<TOriginal, TResult>()
        {
            MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.As) && m.ReturnType.IsByRef);
            return method.MakeGenericMethod(typeof(TOriginal), typeof(TResult));
        }

#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type
        public unsafe static MethodInfo GetCastMethodInfo<TOriginal, TResult>() where TResult : struct
        {
            MethodInfo method = typeof(UnsafeEmit).GetMethod(nameof(UnsafeEmit.ReinterpretCast));
            return method.MakeGenericMethod(typeof(TOriginal), typeof(TResult));
        }
        public unsafe static Span<TResult> ReinterpretCast<TOriginal, TResult>(Span<TOriginal> original)
            where TOriginal : struct
            where TResult : struct
        {
            Span<TResult> result = Span<TResult>.Empty;
            fixed (TOriginal* ptr = original)
            {
                result = new Span<TResult>(ptr, original.Length * sizeof(TOriginal) / sizeof(TResult));
            }
            return result;
        }
#pragma warning restore CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type

    }

    public static MethodInfo ConvertionImplicit<TFrom, TTo>() => ConvertionImplicit(typeof(TFrom), typeof(TTo));
    public static MethodInfo ConvertionImplicit(Type tfrom, Type tto) => tfrom.GetMethod("op_Implicit", new[] { tto });
    public static MethodInfo ConvertionExplicit<TFrom, TTo>() => ConvertionExplicit(typeof(TFrom), typeof(TTo));
    public static MethodInfo ConvertionExplicit(Type tfrom, Type tto) => tfrom.GetMethod("op_Explicit", new[] { tto });
    public static FieldInfo GetFieldInfo<T>(string name, BindingFlags? flags = null) => GetFieldInfo(typeof(T), name, flags);
    public static FieldInfo GetFieldInfo(Type TypeInstance, string name, BindingFlags? flags = null)
    {
        return flags is null ? TypeInstance.GetField(name) : TypeInstance.GetField(name, flags.Value);
    }
    public static MethodInfo GetPropertyInfo<T>(string name, bool getSetter, out PropertyInfo propInfo, BindingFlags? flags = null)
        => GetPropertyInfo(typeof(T), name, getSetter, out propInfo, flags);
    public static MethodInfo GetPropertyInfo(Type typeInstance, string name, bool getSetter, out PropertyInfo propInfo, BindingFlags? flags = null)
    {
        propInfo = flags is null ? typeInstance.GetProperty(name) : typeInstance.GetProperty(name, flags.Value);
        return getSetter ? propInfo.GetSetMethod() : propInfo.GetGetMethod();
    }

    public static void LoadRefArgument<T, U>(this Emit<T> il, ushort index)
        => il.LoadRefArgument(index, typeof(U));

    public static void LoadRefArgument<T>(this Emit<T> il, ushort index, Type targetType)
    {
        il.LoadArgument(index);
        if (targetType.IsValueType)
        {
            il.LoadObject(targetType);
        }
        else
        {
            il.LoadIndirect(targetType);
        }
    }

    public static void Print<T>(this Emit<T> il, Local local)
    {
        if (local.LocalType.IsValueType)
        {
            il.LoadLocalAddress(local);
            il.Call(local.LocalType.GetMethod("ToString", []));
        }
        else
        {
            il.LoadLocal(local);
            il.CallVirtual(local.LocalType.GetMethod("ToString", []));
        }
        il.Call(typeof(Debug).GetMethod(nameof(Debug.Write), [typeof(string)]));
    }
    public static void PrintString<T>(this Emit<T> il, string msg)
    {
        using Local local = il.DeclareLocal<string>();

        il.LoadConstant(msg);
        il.StoreLocal(local);
        il.Print(local);
    }
    

    public static MethodInfo MethodInfo<T>(string name, Type returnType, Type[] argTypes, BindingFlags flags = BindingFlags.Public)
    {
        return typeof(T).GetMethods().First(m => m.Name == name && m.ReturnType == returnType && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(argTypes));
    }

    public static void FindCorrectBranchAndJump<T>(this Emit<T> il, Local jmpDestination, Dictionary<int, Sigil.Label> jumpDestinations, Dictionary<EvmExceptionType, Sigil.Label> evmExceptionLabels)
    {
        int numberOfBitsSet = BitOperations.Log2((uint)jumpDestinations.Count) + 1;

        int length = 1 << numberOfBitsSet;
        int bitMask = length - 1;
        Sigil.Label[] jumps = new Sigil.Label[length];
        for (int i = 0; i < length; i++)
        {
            jumps[i] = il.DefineLabel();
        }

        il.LoadLocal(jmpDestination);
        il.LoadConstant(bitMask);
        il.And();


        il.Switch(jumps);

        for (int i = 0; i < length; i++)
        {
            il.MarkLabel(jumps[i]);
            // for each destination matching the bit mask emit check for the equality
            foreach (ushort dest in jumpDestinations.Keys.Where(dest => (dest & bitMask) == i))
            {
                il.LoadLocal(jmpDestination);
                il.LoadConstant(dest);
                il.BranchIfEqual(jumpDestinations[dest]);
            }
            // each bucket ends with a jump to invalid access to do not fall through to another one
            il.Branch(evmExceptionLabels[EvmExceptionType.InvalidJumpDestination]);
        }
    }

    // requires a zeroed WORD on the stack
    public static void SpecialPushOpcode<T>(this Emit<T> il, OpcodeInfo op, byte[][] data)
    {
        uint count = op.Operation - Instruction.PUSH0;
        uint argSize = BitOperations.RoundUpToPowerOf2(count);

        int argIndex = op.Arguments.Value;
        byte[] zpbytes = data[argIndex].AsSpan().SliceWithZeroPadding(0, (int)argSize, PadDirection.Left).ToArray();

        switch (op.Operation)
        {
            case Instruction.PUSH1:
                il.LoadConstant(zpbytes[0]);
                il.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                break;
            case Instruction.PUSH2:
                il.LoadConstant(BinaryPrimitives.ReadUInt16BigEndian(zpbytes));
                il.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                break;
            case Instruction.PUSH3:
            case Instruction.PUSH4:
                il.LoadConstant(BinaryPrimitives.ReadUInt32BigEndian(zpbytes));
                il.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                break;
            case Instruction.PUSH5:
            case Instruction.PUSH6:
            case Instruction.PUSH7:
            case Instruction.PUSH8:
                il.LoadConstant(BinaryPrimitives.ReadUInt64BigEndian(zpbytes));
                il.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                break;
        }
    }

    public static void EmitAsSpan<T>(this Emit<T> il)
    {
        MethodInfo method = typeof(System.MemoryExtensions).GetMethods()
            .Where(m => m.Name == nameof(System.MemoryExtensions.AsSpan)
                        && m.GetParameters().Length == 3
                        && m.IsGenericMethod)
            .First();
        il.Call(method.MakeGenericMethod(typeof(byte)));
    }


    public static void WhileBranch<T>(this Emit<T> il, Local cond, Action<Emit<T>, Local> action)
    {
        var start = il.DefineLabel();
        var end = il.DefineLabel();

        // start of the loop
        il.MarkLabel(start);

        // if cond
        il.LoadLocal(cond);
        il.BranchIfFalse(end);

        // emit body of loop
        action(il, cond);

        // jump to start of the loop
        il.Branch(start);

        // end of the loop
        il.MarkLabel(end);
    }

    public static void ForBranch<T>(this Emit<T> il, Local count, Action<Emit<T>, Local> action)
    {
        var start = il.DefineLabel();
        var end = il.DefineLabel();

        // declare i
        var i = il.DeclareLocal<int>();

        // we initialize i to 0
        il.LoadConstant(0);
        il.StoreLocal(i);

        // start of the loop
        il.MarkLabel(start);

        // i < count
        il.LoadLocal(i);
        il.LoadLocal(count);
        il.BranchIfEqual(end);

        // emit body of loop
        action(il, i);

        // i++
        il.LoadLocal(i);
        il.LoadConstant(1);
        il.Add();
        il.StoreLocal(i);

        // jump to start of the loop
        il.Branch(start);

        // end of the loop
        il.MarkLabel(end);
    }

    public static void LoadArray<T>(this Emit<T> il, ReadOnlySpan<byte> value)
    {
        il.LoadConstant(value.Length);
        il.NewArray<byte>();

        // get methodInfo of AsSpan from int[] it is a public instance method

        for (int i = 0; i < value.Length; i++)
        {
            il.Duplicate();
            il.LoadConstant(i);
            il.LoadConstant(value[i]);
            il.StoreElement<byte>();
        }

        il.Call(typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(byte[]) }));

    }
    public static void FakeBranch<T>(this Emit<T> il, Sigil.Label label)
    {
        il.LoadConstant(true);
        il.BranchIfTrue(label);
    }

    public static Sigil.Label AddExceptionLabel<T>(this Emit<T> il, Dictionary<EvmExceptionType, Sigil.Label> dict, EvmExceptionType evmExceptionType)
    {
        if(!dict.ContainsKey(evmExceptionType)) {
            dict[evmExceptionType] = il.DefineLabel();
        }
        return dict[evmExceptionType];
    }
}