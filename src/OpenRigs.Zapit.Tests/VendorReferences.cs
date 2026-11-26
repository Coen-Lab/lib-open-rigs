using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace OpenRigs.VendorGuard
{
    /// <summary>
    /// Finds which types of a built assembly refer to a vendor library's types (NI-DAQmx, pylon, and the Bonsai
    /// wrappers that expose them), reading metadata only, so no vendor assembly is ever loaded. A reference is a
    /// base type, interface, field, property, event, method signature, custom attribute, generic constraint,
    /// local variable, catch type, or a type, method or field token in a method body.
    /// </summary>
    internal static class VendorReferences
    {
        // "[assembly]Namespace.Type" is how a type is written here; a vendor type is one whose assembly or namespace is a vendor's.
        static readonly Regex Vendor = new Regex(
            @"\[(NationalInstruments|Basler|Bonsai\.DAQmx|Bonsai\.Pylon)[\].]|\](NationalInstruments|Basler|Bonsai\.DAQmx|Bonsai\.Pylon)\.");

        static readonly Dictionary<ushort, OpCode> OpCodeTable = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null))
            .ToDictionary(o => (ushort)o.Value);

        /// <summary>Each vendor reference as "OuterType: Member -> [assembly]Vendor.Type".</summary>
        public static List<Hit> Find(string assemblyPath)
        {
            var hits = new List<Hit>();
            using (var stream = System.IO.File.OpenRead(assemblyPath))
            using (var pe = new PEReader(stream))
            {
                var md = pe.GetMetadataReader();
                var scan = new Scanner(md);
                foreach (var handle in md.TypeDefinitions)
                {
                    var type = md.GetTypeDefinition(handle);
                    var outer = Outermost(md, type);
                    var owner = Qualified(md, outer);
                    var typeName = Qualified(md, type);

                    void Check(string member, string reference)
                    {
                        if (reference != null && Vendor.IsMatch(reference))
                            hits.Add(new Hit(owner, typeName + "." + member, reference));
                    }

                    if (!type.BaseType.IsNil) Check("(base type)", scan.Entity(type.BaseType));
                    foreach (var i in type.GetInterfaceImplementations())
                        Check("(interface)", scan.Entity(md.GetInterfaceImplementation(i).Interface));
                    CheckGenerics(md, type.GetGenericParameters(), scan, Check, "(type constraint)");
                    CheckAttributes(md, type.GetCustomAttributes(), scan, Check, "(type attribute)");

                    foreach (var f in type.GetFields())
                    {
                        var field = md.GetFieldDefinition(f);
                        var name = md.GetString(field.Name);
                        Check(name + " (field)", field.DecodeSignature(scan.Types, null));
                        CheckAttributes(md, field.GetCustomAttributes(), scan, Check, name + " (field attribute)");
                    }
                    foreach (var p in type.GetProperties())
                    {
                        var prop = md.GetPropertyDefinition(p);
                        var name = md.GetString(prop.Name);
                        var sig = prop.DecodeSignature(scan.Types, null);
                        Check(name + " (property)", Join(sig.ReturnType, sig.ParameterTypes));
                        CheckAttributes(md, prop.GetCustomAttributes(), scan, Check, name + " (property attribute)");
                    }
                    foreach (var e in type.GetEvents())
                    {
                        var ev = md.GetEventDefinition(e);
                        Check(md.GetString(ev.Name) + " (event)", scan.Entity(ev.Type));
                    }
                    foreach (var m in type.GetMethods())
                    {
                        var method = md.GetMethodDefinition(m);
                        var name = md.GetString(method.Name);
                        var sig = method.DecodeSignature(scan.Types, null);
                        Check(name + " (signature)", Join(sig.ReturnType, sig.ParameterTypes));
                        CheckGenerics(md, method.GetGenericParameters(), scan, Check, name + " (constraint)");
                        CheckAttributes(md, method.GetCustomAttributes(), scan, Check, name + " (attribute)");
                        if (method.RelativeVirtualAddress == 0) continue;
                        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                        if (!body.LocalSignature.IsNil)
                            foreach (var local in md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(scan.Types, null))
                                Check(name + " (local)", local);
                        foreach (var region in body.ExceptionRegions)
                            if (!region.CatchType.IsNil) Check(name + " (catch)", scan.Entity(region.CatchType));
                        foreach (var token in Tokens(body.GetILBytes()))
                            Check(name + " (IL)", scan.Entity(MetadataTokens.EntityHandle(token)));
                    }
                }
            }
            return hits;
        }

        public sealed class Hit
        {
            public Hit(string owner, string member, string reference)
            {
                Owner = owner;
                Member = member;
                Reference = reference;
            }

            /// <summary>The outermost type, so a nested type counts as part of the type it sits in.</summary>
            public string Owner { get; }
            public string Member { get; }
            public string Reference { get; }
            public override string ToString() => Member + " refers to " + Reference;
        }

        static string Join(string first, IEnumerable<string> rest) => string.Join(" ", new[] { first }.Concat(rest));

        static void CheckAttributes(MetadataReader md, CustomAttributeHandleCollection attributes, Scanner scan, Action<string, string> check, string what)
        {
            foreach (var a in attributes) check(what, scan.Entity(md.GetCustomAttribute(a).Constructor));
        }

        static void CheckGenerics(MetadataReader md, GenericParameterHandleCollection parameters, Scanner scan, Action<string, string> check, string what)
        {
            foreach (var g in parameters)
                foreach (var c in md.GetGenericParameter(g).GetConstraints())
                    check(what, scan.Entity(md.GetGenericParameterConstraint(c).Type));
        }

        static TypeDefinition Outermost(MetadataReader md, TypeDefinition type)
        {
            while (!type.GetDeclaringType().IsNil) type = md.GetTypeDefinition(type.GetDeclaringType());
            return type;
        }

        static string Qualified(MetadataReader md, TypeDefinition type)
        {
            var ns = md.GetString(type.Namespace);
            var name = md.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            if (!declaring.IsNil) return Qualified(md, md.GetTypeDefinition(declaring)) + "+" + name;
            return ns.Length == 0 ? name : ns + "." + name;
        }

        // The metadata tokens an IL stream's instructions carry (calls, field accesses, newobj, castclass, ldtoken, ...).
        static IEnumerable<int> Tokens(byte[] il)
        {
            var i = 0;
            while (i < il.Length)
            {
                ushort code = il[i++];
                if (code == 0xFE) code = (ushort)(0xFE00 | il[i++]);
                var op = OpCodeTable[code];
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: i += 1; break;
                    case OperandType.InlineVar: i += 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: i += 8; break;
                    case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineSig:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        yield return BitConverter.ToInt32(il, i);
                        i += 4;
                        break;
                    default: i += 4; break;
                }
            }
        }

        sealed class Scanner
        {
            readonly MetadataReader md;

            public Scanner(MetadataReader md)
            {
                this.md = md;
                Types = new TypeNames();
            }

            public TypeNames Types { get; }

            // Any entity a signature or an instruction can name, as "[assembly]Type" or "[assembly]Type::member signature".
            public string Entity(EntityHandle h)
            {
                switch (h.Kind)
                {
                    case HandleKind.TypeDefinition:
                        return Types.GetTypeFromDefinition(md, (TypeDefinitionHandle)h, 0);
                    case HandleKind.TypeReference:
                        return Types.GetTypeFromReference(md, (TypeReferenceHandle)h, 0);
                    case HandleKind.TypeSpecification:
                        return Types.GetTypeFromSpecification(md, null, (TypeSpecificationHandle)h, 0);
                    case HandleKind.MemberReference:
                        var member = md.GetMemberReference((MemberReferenceHandle)h);
                        string signature;
                        if (member.GetKind() == MemberReferenceKind.Field) signature = member.DecodeFieldSignature(Types, null);
                        else
                        {
                            var s = member.DecodeMethodSignature(Types, null);
                            signature = Join(s.ReturnType, s.ParameterTypes);
                        }
                        return Entity(member.Parent) + "::" + md.GetString(member.Name) + " " + signature;
                    case HandleKind.MethodSpecification:
                        var spec = md.GetMethodSpecification((MethodSpecificationHandle)h);
                        return Entity(spec.Method) + " " + string.Join(" ", spec.DecodeSignature(Types, null));
                    case HandleKind.StandaloneSignature:
                        var sig = md.GetStandaloneSignature((StandaloneSignatureHandle)h);
                        if (sig.GetKind() != StandaloneSignatureKind.Method) return null;
                        var m = sig.DecodeMethodSignature(Types, null);
                        return Join(m.ReturnType, m.ParameterTypes);
                    default:
                        // A method or field defined in this assembly: its own signature is checked where it is declared.
                        return null;
                }
            }
        }

        sealed class TypeNames : ISignatureTypeProvider<string, object>
        {
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
                "[this]" + Qualified(reader, reader.GetTypeDefinition(handle));

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var r = reader.GetTypeReference(handle);
                var name = reader.GetString(r.Name);
                switch (r.ResolutionScope.Kind)
                {
                    case HandleKind.AssemblyReference:
                        var ns = reader.GetString(r.Namespace);
                        var asm = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)r.ResolutionScope).Name);
                        return "[" + asm + "]" + (ns.Length == 0 ? name : ns + "." + name);
                    case HandleKind.TypeReference:
                        return GetTypeFromReference(reader, (TypeReferenceHandle)r.ResolutionScope, 0) + "+" + name;
                    default:
                        return "[this]" + name;
                }
            }

            public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
                reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType + " " + modifier;
            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
                genericType + "<" + string.Join(",", typeArguments) + ">";
            public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
            public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr " + Join(signature.ReturnType, signature.ParameterTypes);
        }
    }
}
