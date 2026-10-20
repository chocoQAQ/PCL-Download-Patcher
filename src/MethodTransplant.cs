using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PclDownloadPatcher
{
    // Symbolic references, rather than copied metadata tokens, keep all calls
    // bound to the target launcher's existing types, fields and private methods.
    internal sealed class MethodTransplant
    {
        private readonly ModuleDefinition module;
        private readonly Dictionary<string, TypeReference> types = new Dictionary<string, TypeReference>();
        private readonly Dictionary<string, MethodReference> methods = new Dictionary<string, MethodReference>();
        private readonly Dictionary<string, FieldReference> fields = new Dictionary<string, FieldReference>();

        internal static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (var type in module.Types)
                foreach (var item in Walk(type)) yield return item;
        }

        private static IEnumerable<TypeDefinition> Walk(TypeDefinition type)
        {
            yield return type;
            foreach (var nested in type.NestedTypes)
                foreach (var item in Walk(nested)) yield return item;
        }

        internal MethodTransplant(ModuleDefinition target)
        {
            module = target;
            foreach (var type in target.GetTypeReferences()) types[type.FullName] = type;
            foreach (var member in target.GetMemberReferences()) Register(member);
            foreach (var type in AllTypes(target))
            {
                types[type.FullName] = type;
                foreach (var field in type.Fields) fields[field.FullName] = field;
                foreach (var method in type.Methods)
                {
                    methods[method.FullName] = method;
                    if (method.HasBody)
                        foreach (var instruction in method.Body.Instructions)
                        {
                            var member = instruction.Operand as MemberReference;
                            if (member != null && !(member is MethodDefinition) && !(member is FieldDefinition)) Register(member);
                        }
                }
            }
            // Definitions take precedence over MemberRefs imported from another module.
            foreach (var type in AllTypes(target))
            {
                foreach (var method in type.Methods) methods[method.FullName] = method;
                foreach (var field in type.Fields) fields[field.FullName] = field;
            }
        }

        private void Register(MemberReference member)
        {
            var method = member as MethodReference;
            var field = member as FieldReference;
            var type = member as TypeReference;
            if (method != null && !methods.ContainsKey(method.FullName)) methods[method.FullName] = method;
            if (field != null && !fields.ContainsKey(field.FullName)) fields[field.FullName] = field;
            if (type != null && !types.ContainsKey(type.FullName)) types[type.FullName] = type;
        }

        private TypeReference ImportType(TypeReference source)
        {
            TypeReference result;
            if (types.TryGetValue(source.FullName, out result)) return result;
            return module.ImportReference(source);
        }

        private object ImportOperand(object operand, MethodDefinition target,
            Dictionary<Instruction, Instruction> instructions, Dictionary<VariableDefinition, VariableDefinition> variables)
        {
            if (operand == null) return null;
            var instruction = operand as Instruction;
            if (instruction != null) return instructions[instruction];
            var branches = operand as Instruction[];
            if (branches != null) return branches.Select(item => instructions[item]).ToArray();
            var variable = operand as VariableDefinition;
            if (variable != null) return variables[variable];
            var parameter = operand as ParameterDefinition;
            if (parameter != null) return parameter.Index < 0 ? target.Body.ThisParameter : target.Parameters[parameter.Index];
            var method = operand as MethodReference;
            if (method != null)
            {
                MethodReference existing;
                if (methods.TryGetValue(method.FullName, out existing)) return existing;
                return module.ImportReference(method);
            }
            var field = operand as FieldReference;
            if (field != null)
            {
                FieldReference existing;
                if (fields.TryGetValue(field.FullName, out existing)) return existing;
                return module.ImportReference(field);
            }
            var type = operand as TypeReference;
            if (type != null) return ImportType(type);
            if (operand is CallSite) throw new NotSupportedException("Unexpected calli signature in patch payload.");
            if (operand is string || operand.GetType().IsPrimitive) return operand;
            throw new NotSupportedException("Unexpected IL operand: " + operand.GetType().Name);
        }

        internal void Copy(MethodDefinition source, MethodDefinition target)
        {
            if (!source.HasBody || source.FullName != target.FullName) throw new InvalidOperationException("Patch method identity mismatch.");
            var body = new MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
            target.Body = body;
            var variables = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (var variable in source.Body.Variables)
            {
                var copy = new VariableDefinition(ImportType(variable.VariableType));
                body.Variables.Add(copy);
                variables.Add(variable, copy);
            }
            var instructions = new Dictionary<Instruction, Instruction>();
            foreach (var instruction in source.Body.Instructions)
            {
                var copy = Instruction.Create(OpCodes.Nop);
                copy.OpCode = instruction.OpCode;
                body.Instructions.Add(copy);
                instructions.Add(instruction, copy);
            }
            foreach (var instruction in source.Body.Instructions)
                instructions[instruction].Operand = ImportOperand(instruction.Operand, target, instructions, variables);
            foreach (var handler in source.Body.ExceptionHandlers)
                body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType) {
                    TryStart = instructions[handler.TryStart],
                    TryEnd = handler.TryEnd == null ? null : instructions[handler.TryEnd],
                    HandlerStart = instructions[handler.HandlerStart],
                    HandlerEnd = handler.HandlerEnd == null ? null : instructions[handler.HandlerEnd],
                    FilterStart = handler.FilterStart == null ? null : instructions[handler.FilterStart],
                    CatchType = handler.CatchType == null ? null : ImportType(handler.CatchType)
                });
        }
    }
}
