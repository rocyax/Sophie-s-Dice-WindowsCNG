using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

public static class DicePatch
{
    public static void Apply(string input, string output, byte[] helper, Assembly editor)
    {
        using (var game = ModuleDefMD.Load(input, ModuleDef.CreateModuleContext()))
        {
            MergeWithDnSpyEx(game, helper, editor);
            var random = game.Find("CryptoRand", false);
            var roll = game.Find("RollRandom", false);
            var reader = roll.NestedTypes.Single(t => t.Name == "Reader");

            int unity = RedirectUnity(game, random);
            PatchExpression(game, roll, reader);
            PatchReplacement(game, roll);
            Require(unity == 46, "Unity random call count: " + unity);
            Require(Calls(game, "System.Random").Count() == 0, "System.Random still in use");

            var options = new ModuleWriterOptions(game);
            options.MetadataOptions.Flags |= MetadataFlags.PreserveTypeDefRids;
            game.Write(output, options);
            Console.WriteLine("Redirected {0} Unity random calls and replaced both System.Random paths.", unity);
        }
    }

    // Use the importer behind dnSpyEx's Merge with Assembly command.
    private static void MergeWithDnSpyEx(ModuleDef game, byte[] helper, Assembly editor)
    {
        var importerType = editor.GetType("dnSpy.AsmEditor.Compiler.ModuleImporter", true);
        var optionsType = editor.GetType("dnSpy.AsmEditor.Compiler.ModuleImporterOptions", true);
        var importer = Activator.CreateInstance(importerType, game, game.Context.AssemblyResolver);
        var import = importerType.GetMethods().Single(m => m.Name == "Import"
            && m.GetParameters()[2].ParameterType == optionsType);
        var debug = Activator.CreateInstance(import.GetParameters()[1].ParameterType);
        import.Invoke(importer, new[] { helper, debug, Enum.ToObject(optionsType, 0) });

        var diagnostics = (Array)importerType.GetProperty("Diagnostics").GetValue(importer);
        Require(diagnostics.Length == 0, "dnSpyEx import: " + string.Join(", ", diagnostics.Cast<object>()));
        var imported = (Array)importerType.GetProperty("NewNonNestedTypes").GetValue(importer);
        Require(imported.Length == 2, "Unexpected helper types: " + string.Join(", ",
            imported.Cast<object>().Select(item => item.GetType().GetProperty("TargetType").GetValue(item))));
        foreach (var item in imported)
            game.Types.Add((TypeDef)item.GetType().GetProperty("TargetType").GetValue(item));
    }

    private static IEnumerable<(MethodDef Method, Instruction Call)> Calls(ModuleDef game, string type)
    {
        foreach (var method in game.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is IMethod target && target.DeclaringType.FullName == type)
                    yield return (method, instruction);
    }

    private static int RedirectUnity(ModuleDef game, TypeDef random)
    {
        var integerRange = random.Methods.Single(m => m.Name == "Range" && m.MethodSig.RetType.ElementType == ElementType.I4);
        var floatRange = random.Methods.Single(m => m.Name == "Range" && m.MethodSig.RetType.ElementType == ElementType.R4);
        var inclusiveRange = random.FindMethod("RangeInclusive");
        int patched = 0;
        int inclusive = 0;

        foreach (var entry in Calls(game, "UnityEngine.Random"))
        {
            var method = entry.Method;
            string type = method.DeclaringType.FullName;
            string name = method.Name;
            if (type == "DiceExpression" && (name == "Prepare_Instanced" || name == "ResetLocalRandomFunctionSeed"))
                continue;

            bool isRoll = type == "DieObject" && name != "OnCollisionEnter"
                || type == "Die" && name == "RandomAppearance" && ((IMethod)entry.Call.Operand).Name == "get_value"
                || type == "DieDragger/ClickyInterface"
                || type == "DieResult" || type == "DieFace" || type == "DicePoolApproximator"
                || type == "DExpressionComponent" || type == "DiceExpression";
            if (!isRoll)
                continue;

            var target = (IMethod)entry.Call.Operand;
            switch (target.Name.String)
            {
                case "Range":
                    var instructions = method.Body.Instructions;
                    int index = instructions.IndexOf(entry.Call);
                    if ((type == "DieResult" || type == "DieFace") && index >= 2
                        && instructions[index - 1].OpCode == OpCodes.Add
                        && instructions[index - 2].OpCode == OpCodes.Ldc_I4_1)
                    {
                        Clear(instructions[index - 2]);
                        Clear(instructions[index - 1]);
                        entry.Call.Operand = inclusiveRange;
                        inclusive++;
                    }
                    else
                    {
                        entry.Call.Operand = target.MethodSig.RetType.ElementType == ElementType.I4
                            ? integerRange : floatRange;
                    }
                    break;
                case "get_value":
                    entry.Call.Operand = random.FindMethod("get_Value");
                    break;
                case "get_rotationUniform":
                    entry.Call.Operand = random.FindMethod("get_RotationUniform");
                    break;
                default:
                    throw new InvalidDataException("Unexpected Unity random method: " + target.FullName);
            }
            patched++;
        }
        Require(inclusive == 4, "Inclusive range call count: " + inclusive);
        return patched;
    }

    private static void PatchExpression(ModuleDef game, TypeDef roll, TypeDef reader)
    {
        var expression = game.Find("DiceExpression", false);
        var component = game.Find("DExpressionComponent", false);
        var expressionRoll = AddField(expression, "cngExpressionRoll", roll.ToTypeSig());
        var componentRoll = AddField(component, "cngComponentRoll", roll.ToTypeSig());
        var expressionSeed = expression.FindField("randomFunctionSeed");
        var componentSeed = component.FindField("componentSeed");
        var rollCtor = roll.FindMethod(".ctor");

        int resets = 0;
        foreach (var name in new[] { "Prepare_Instanced", "ResetLocalRandomFunctionSeed" })
        {
            var instructions = expression.FindMethod(name).Body.Instructions;
            for (int i = 3; i < instructions.Count; i++)
            {
                var field = instructions[i].Operand as FieldDef;
                if (instructions[i].OpCode != OpCodes.Stfld || (field != expressionSeed && field != componentSeed))
                    continue;

                Require(instructions[i - 1].Operand is IMethod range && range.DeclaringType.FullName == "UnityEngine.Random"
                    && instructions[i - 3].GetLdcI4Value() == int.MinValue
                    && instructions[i - 2].GetLdcI4Value() == int.MaxValue, "Unexpected expression roll reset");
                Clear(instructions[i - 3]);
                Clear(instructions[i - 2]);
                instructions[i - 1].OpCode = OpCodes.Newobj;
                instructions[i - 1].Operand = rollCtor;
                instructions[i].Operand = field == expressionSeed ? expressionRoll : componentRoll;
                resets++;
            }
        }
        Require(resets == 4, "Expression roll reset count: " + resets);

        var parser = game.Find("B83.ExpressionParser.ExpressionParser", false);
        var oldCtor = parser.FindMethod(".ctor");
        var parserCtor = new MethodDefUser(".ctor", MethodSig.CreateInstance(game.CorLibTypes.Void, roll.ToTypeSig()),
            oldCtor.ImplAttributes, (oldCtor.Attributes & ~dnlib.DotNet.MethodAttributes.MemberAccessMask) | dnlib.DotNet.MethodAttributes.Assembly);
        parserCtor.Body = oldCtor.Body;
        parser.Methods.Add(parserCtor);

        var parserSeed = parser.FindField("expressionRNGSeed");
        var init = parserCtor.Body.Instructions;
        int seedStore = init.ToList().FindLastIndex(i => i.Operand == parserSeed);
        Require(seedStore >= 2 && init[seedStore - 2].OpCode == OpCodes.Ldarg_0
            && init[seedStore - 1].OpCode == OpCodes.Ldarg_1, "Unexpected parser seed store");
        for (int i = seedStore - 2; i <= seedStore; i++)
            Clear(init[i]);

        var rnd = parser.NestedTypes.SelectMany(t => t.Fields).Single(f => f.Name == "rnd");
        rnd.FieldSig = new FieldSig(reader.ToTypeSig());
        var randomCtor = init.Single(i => i.Operand is IMethod m && m.DeclaringType.FullName == "System.Random");
        randomCtor.OpCode = OpCodes.Call;
        randomCtor.Operand = roll.FindMethod("CreateReader");
        int draws = 0;
        foreach (var method in rnd.DeclaringType.Methods.Where(m => m.HasBody))
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is IMethod m && m.DeclaringType.FullName == "System.Random" && m.Name == "NextDouble")
                {
                    instruction.Operand = reader.FindMethod("NextDouble");
                    draws++;
                }
        Require(draws == 3, "rnd() call count: " + draws);

        // Keep the original constructor for the parser's standalone entry points.
        oldCtor.Body = new CilBody();
        Append(oldCtor.Body, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldnull),
            Instruction.Create(OpCodes.Call, parserCtor), Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Stfld, parserSeed), Instruction.Create(OpCodes.Ret));

        var eval = expression.FindMethod("Evaluate_Instanced").Body.Instructions;
        var call = eval.Single(i => i.Operand == oldCtor);
        var load = eval[eval.IndexOf(call) - 1];
        Require(load.OpCode == OpCodes.Ldfld && load.Operand == expressionSeed, "Unexpected parser call");
        load.Operand = expressionRoll;
        call.Operand = parserCtor;
    }

    private static void PatchReplacement(ModuleDef game, TypeDef roll)
    {
        var result = game.Find("DieResult", false);
        var source = AddField(result, "cngRollIdentity", game.CorLibTypes.Object);
        var copy = result.FindMethod("MakeCopy").Body;
        InsertBefore(copy, copy.Instructions.Last(), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, source), Instruction.Create(OpCodes.Call, roll.FindMethod("GetRollIdentity")),
            Instruction.Create(OpCodes.Stfld, source));
        var prepare = result.FindMethod("PrepareForNewRoll").Body;
        var resetFlag = prepare.Instructions.Single(i => i.OpCode == OpCodes.Stfld && i.Operand == result.FindField("evaluatingSubExpression"));
        var resetStart = prepare.Instructions[prepare.Instructions.IndexOf(resetFlag) - 2];
        Require(resetStart.OpCode == OpCodes.Ldarg_0, "Unexpected roll reset");
        // Keep existing branches targeting the first instruction after the pinned-die guard.
        InsertBefore(prepare, prepare.Instructions[prepare.Instructions.IndexOf(resetStart) + 1],
            Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Stfld, source), Instruction.Create(OpCodes.Ldarg_0));
        prepare.SimplifyBranches();

        var component = game.Find("DExpressionComponent", false);
        var eval = component.FindMethod("ResultConditionUpdate").Body;
        var oldUpdate = game.Find("RollCondition_ValueReplace", false).FindMethod("ConditionUpdate");
        var call = eval.Instructions.Single(i => i.Operand == oldUpdate);
        var load = eval.Instructions[eval.Instructions.IndexOf(call) - 1];
        Require(load.OpCode == OpCodes.Ldfld && load.Operand == component.FindField("componentSeed"),
            "Unexpected replacement condition call");
        load.OpCode = OpCodes.Ldflda;
        load.Operand = component.FindField("cngComponentRoll");
        var conditionIndex = eval.Instructions[eval.Instructions.IndexOf(call) - 6].Operand as Local;
        Require(conditionIndex != null && conditionIndex.Type.ElementType == ElementType.I4, "Missing condition index");
        InsertBefore(eval, call, Instruction.Create(OpCodes.Ldloc, conditionIndex));

        var update = new MethodDefUser("ConditionUpdate",
            MethodSig.CreateInstance(game.CorLibTypes.Void, oldUpdate.MethodSig.Params[0],
                new ByRefSig(roll.ToTypeSig()), game.CorLibTypes.Int32),
            oldUpdate.ImplAttributes, (oldUpdate.Attributes & ~dnlib.DotNet.MethodAttributes.MemberAccessMask) | dnlib.DotNet.MethodAttributes.Assembly);
        update.Body = oldUpdate.Body;
        update.ParamDefs.Add(new ParamDefUser("dieResults", 1));
        update.ParamDefs.Add(new ParamDefUser("roll", 2));
        update.ParamDefs.Add(new ParamDefUser("conditionIndex", 3));
        oldUpdate.DeclaringType.Methods.Add(update);
        call.Operand = update;

        oldUpdate.Body = new CilBody { InitLocals = true };
        var standaloneRoll = new Local(roll.ToTypeSig());
        oldUpdate.Body.Variables.Add(standaloneRoll);
        Append(oldUpdate.Body, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldloca, standaloneRoll), Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Call, update), Instruction.Create(OpCodes.Ret));
        var body = update.Body;
        var il = body.Instructions;
        int constructor = il.ToList().FindIndex(i => i.Operand is IMethod m && m.DeclaringType.FullName == "System.Random" && m.Name == ".ctor");
        int store = il.ToList().FindIndex(constructor, i => i.OpCode == OpCodes.Stfld && i.Operand == result.FindField("currentValue"));
        int first = constructor - 3;
        Require(il[first].OpCode == OpCodes.Ldarg_2 && il[store - 1].Operand is IMethod floor && floor.Name == "FloorToInt",
            "Unexpected random replacement calculation");
        var getDie = (IMethod)il[first - 3].Operand;
        var rules = update.DeclaringType.FindField("replacementVals");
        var getRule = (IMethod)il[first + 10].Operand;
        var replacement = game.Find("ExpValue", false).FindField("replacementVal");
        Require(getDie.Name == "get_Item" && getRule.Name == "get_Item", "Unexpected replacement list lookup");

        // Replace only the random range calculation. Keep the surrounding comparison and modifier order.
        var end = il[store + 1];
        for (int i = first; i <= store; i++)
            Clear(il[i]);
        InsertBefore(body, end,
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Callvirt, getDie),
            Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldfld, source),
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, rules),
            Instruction.Create(OpCodes.Ldloc_1), Instruction.Create(OpCodes.Callvirt, getRule), Instruction.Create(OpCodes.Ldfld, replacement),
            Instruction.Create(OpCodes.Ldarg_2), Instruction.Create(OpCodes.Ldarg_3), Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Call, roll.FindMethod("ApplyReplacement")));
        body.SimplifyBranches();
        eval.SimplifyBranches();
    }

    private static FieldDef AddField(TypeDef type, string name, TypeSig signature)
    {
        var field = new FieldDefUser(name, new FieldSig(signature), dnlib.DotNet.FieldAttributes.Assembly);
        type.Fields.Add(field);
        return field;
    }

    private static void Append(CilBody body, params Instruction[] instructions)
    {
        foreach (var instruction in instructions)
            body.Instructions.Add(instruction);
    }

    private static void InsertBefore(CilBody body, Instruction target, params Instruction[] instructions)
    {
        int index = body.Instructions.IndexOf(target);
        foreach (var instruction in instructions)
            body.Instructions.Insert(index++, instruction);
    }

    private static void Clear(Instruction instruction)
    {
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
