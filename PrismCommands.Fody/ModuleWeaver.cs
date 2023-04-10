﻿using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using PrismCommands.Fody.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

public class ModuleWeaver : BaseModuleWeaver
{
    private const string DelegateCommandAttributeName = "DelegateCommandAttribute";
    private const string CommandBackingFieldNameFormat = "<{0}>k__BackingField";
    private const string GetCommandMethodNameFormat = "get_{0}";
    private const string CommandMethodNameFormat = "{0}Command";

    private TypeReference _delegateCommandType;

    public override IEnumerable<string> GetAssembliesForScanning()
    {
        yield return "Prism";
    }

    public override void Execute()
    {
        _delegateCommandType = ModuleDefinition.ImportReference("Prism.Commands.DelegateCommand", "Prism");

        foreach (var method in ModuleDefinition.Types.SelectMany(type => type.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name == DelegateCommandAttributeName)).ToList()))
        {
            RemoveDelegateCommandAttribute(method);

            var commandField = CreateBackingFieldForCommand(method);
            AddAttributesToBackingField(commandField);
            AddBackingFieldToType(method.DeclaringType, commandField);

            var canExecuteMethod = FindCanExecuteMethod(method);

            MethodDefinition delegateCommandCtor;

            if (canExecuteMethod != null)
            {
                delegateCommandCtor = FindDelegateCommandConstructor(true);
            }
            else
            {
                delegateCommandCtor = FindDelegateCommandConstructor(false);
            }

            var commandProperty = CreateCommandProperty(method, commandField);
            
            UpdateConstructor(method.DeclaringType, method, commandField, delegateCommandCtor, canExecuteMethod);

            method.DeclaringType.Properties.Add(commandProperty);
            method.DeclaringType.Methods.Add(commandProperty.GetMethod);
            MakeMethodPrivate(method);
        }
    }

    private MethodDefinition FindCanExecuteMethod(MethodDefinition method)
    {
        return method.DeclaringType.Methods.FirstOrDefault(m => m.Name == $"Can{method.Name}" && m.ReturnType.MetadataType == MetadataType.Boolean && !m.HasParameters);
    }

    private void RemoveDelegateCommandAttribute(MethodDefinition method)
    {
        var attribute = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == DelegateCommandAttributeName) ?? throw new WeavingException($"Method '{method.FullName}' does not have a '{DelegateCommandAttributeName}' attribute.");
        method.CustomAttributes.Remove(attribute);
    }

    private FieldDefinition CreateBackingFieldForCommand(MethodDefinition method)
    {
        var commandMethodName = string.Format(CommandMethodNameFormat, method.Name);
        var commandFieldName = string.Format(CommandBackingFieldNameFormat, commandMethodName);
        var commandFieldType = _delegateCommandType;

        return new FieldDefinition(commandFieldName, FieldAttributes.Private | FieldAttributes.InitOnly, commandFieldType);
    }

    private void AddAttributesToBackingField(FieldDefinition commandField)
    {
        commandField.AddAttribute<CompilerGeneratedAttribute>(ModuleDefinition, "System.Runtime");
        commandField.AddAttribute<DebuggerBrowsableAttribute>(ModuleDefinition, "System.Runtime", DebuggerBrowsableState.Never);
    }

    private void AddBackingFieldToType(TypeDefinition type, FieldDefinition commandField)
    {
        type.Fields.Add(commandField);
    }

    private MethodDefinition FindDelegateCommandConstructor(bool hasCanExecuteMethod)
    {
        var delegateCommandConstructors = _delegateCommandType.Resolve().GetConstructors();
        
        MethodDefinition delegateCommandCtor;

        if (hasCanExecuteMethod)
        {
            delegateCommandCtor = delegateCommandConstructors.FirstOrDefault(m => m.Parameters.Count == 2);
        }
        else
        {
            delegateCommandCtor = delegateCommandConstructors.FirstOrDefault(m => m.Parameters.Count == 1 &&
                                     m.Parameters[0].ParameterType.FullName == typeof(Action).FullName);
        }

        if (delegateCommandCtor == null)
        {
            throw new WeavingException($"Unable to find DelegateCommand constructor {(hasCanExecuteMethod ? "with two parameters of types Action and Func`1<Boolean>" : "with a single parameter of type Action")}. Available constructors: {string.Join(", ", delegateCommandConstructors.Select(c => c.ToString()))}");
        }

        return delegateCommandCtor;
    }

    private bool IsFuncOfBool(TypeReference type)
    {
        if (type.IsGenericInstance && type.FullName.StartsWith("System.Func`1"))
        {
            var genericInstanceType = (GenericInstanceType)type;
            return genericInstanceType.GenericArguments[0].FullName == "System.Boolean";
        }

        return false;
    }

    private PropertyDefinition CreateCommandProperty(MethodDefinition method, FieldDefinition commandField)
    {
        var commandMethodName = string.Format(CommandMethodNameFormat, method.Name);
        var getCommandMethodName = string.Format(GetCommandMethodNameFormat, commandMethodName);

        var commandProperty = new PropertyDefinition(commandMethodName, PropertyAttributes.None, commandField.FieldType)
        {
            GetMethod = new MethodDefinition(getCommandMethodName, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, commandField.FieldType)
            {
                Body =
                {
                    Instructions =
                    {
                        Instruction.Create(OpCodes.Ldarg_0),
                        Instruction.Create(OpCodes.Ldfld, commandField),
                        Instruction.Create(OpCodes.Ret)
                    }
                }
            }
        };

        commandProperty.GetMethod.AddAttribute<CompilerGeneratedAttribute>(ModuleDefinition, "System.Runtime");

        return commandProperty;
    }

    private void UpdateConstructor(TypeDefinition type, MethodDefinition method, FieldDefinition commandField, MethodDefinition delegateCommandCtor, MethodDefinition canExecuteMethod = null)
    {
        var ctor = type.GetConstructors().FirstOrDefault() ?? throw new WeavingException($"Unable to find default constructor in the type '{type.FullName}'.");

        var actionType = ModuleDefinition.ImportReference(typeof(Action).FullName, "System.Runtime");
        var actionConstructorInfo = actionType.Resolve().GetConstructors().FirstOrDefault(c => c.Parameters.Count == 2 && c.Parameters[0].ParameterType.MetadataType == MetadataType.Object && c.Parameters[1].ParameterType.MetadataType == MetadataType.IntPtr) ?? throw new WeavingException($"Unable to find Action constructor with two parameters in the type '{actionType.FullName}'.");
        var actionConstructor = ModuleDefinition.ImportReference(actionConstructorInfo);

        var ilCtor = ctor.Body.GetILProcessor();
        var lastRetInstruction = ctor.Body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret) ?? throw new WeavingException($"Constructor '{ctor.FullName}' does not have a return instruction (ret).");

        Instruction[] instructions;

        if (canExecuteMethod != null)
        {
            var funcBoolType = ModuleDefinition.ImportReference(typeof(Func<bool>).GetGenericTypeDefinition().MakeGenericType(typeof(bool)));
            var funcBoolConstructorInfo = funcBoolType.Resolve().GetConstructors().FirstOrDefault(c => c.Parameters.Count == 2 && c.Parameters[0].ParameterType.MetadataType == MetadataType.Object && c.Parameters[1].ParameterType.MetadataType == MetadataType.IntPtr) ?? throw new WeavingException($"Unable to find Func<bool> constructor with two parameters in the type '{funcBoolType.FullName}'.");
            var funcBoolConstructor = ModuleDefinition.ImportReference(funcBoolConstructorInfo);

            instructions = new[]
            {
                Instruction.Create(OpCodes.Nop),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldftn, method),
                Instruction.Create(OpCodes.Newobj, actionConstructor),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldftn, canExecuteMethod),
                Instruction.Create(OpCodes.Newobj, funcBoolConstructor),
                Instruction.Create(OpCodes.Newobj, ModuleDefinition.ImportReference(delegateCommandCtor)),
                Instruction.Create(OpCodes.Stfld, commandField)
            };
        }
        else
        {
            instructions = new[]
            {
                Instruction.Create(OpCodes.Nop),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldftn, method),
                Instruction.Create(OpCodes.Newobj, actionConstructor),
                Instruction.Create(OpCodes.Newobj, ModuleDefinition.ImportReference(delegateCommandCtor)),
                Instruction.Create(OpCodes.Stfld, commandField)
            };
        }

        foreach (var instruction in instructions)
        {
            ilCtor.InsertBefore(lastRetInstruction, instruction);
        }
    }

    private void MakeMethodPrivate(MethodDefinition method)
    {
        method.IsPrivate = true;
    }

    public override bool ShouldCleanReference => true;
}
