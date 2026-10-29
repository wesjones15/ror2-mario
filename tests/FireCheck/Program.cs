using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
class Program
{
    static void Main(string[] args)
    {
        string game = Path.GetFullPath(args[0]);
        string plugin = Path.GetFullPath(args[1]);
        string managed = Path.Combine(game, "Risk of Rain 2_Data", "Managed");
        string core = Path.Combine(game, "BepInEx", "core");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            string file = new AssemblyName(e.Name).Name + ".dll";
            string path = new[]{managed, core, Path.GetDirectoryName(plugin)}.Select(p=>Path.Combine(p,file)).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        var host = Assembly.LoadFrom(plugin);
        var fire = host.GetType("MarioTest.MarioFire", true);
        var patch = fire.GetMethod("PatchTrailDamage", BindingFlags.Static|BindingFlags.NonPublic);
        using var asm = AssemblyDefinition.ReadAssembly(Path.Combine(managed,"RoR2.dll"));
        var trail = asm.MainModule.Types.Single(t=>t.FullName=="RoR2.DamageTrail");
        var damage = trail.Methods.Single(m=>m.Name=="DoDamage");
        int originalBranches = damage.Body.Instructions.Count(i=>i.OpCode==OpCodes.Brtrue);
        using var context = new ILContext(damage);
        patch.Invoke(null,new object[]{context});
        var branches = damage.Body.Instructions.Where(i=>i.OpCode==OpCodes.Brtrue).ToArray();
        if(branches.Length!=originalBranches+1) throw new Exception("No quench branch installed");
        var target = branches.Last().Operand is ILLabel label ? label.Target : (Instruction)branches.Last().Operand;
        if(!target.MatchLdloc(5) || !target.Next.MatchStloc(0) || !target.Next.Next.MatchLdloc(4))
            throw new Exception("Quenched segments skip the previous-point update: dry sections would bridge the gap");
        var branch = branches.Last();
        var range=damage.Body.Instructions.Skip(damage.Body.Instructions.IndexOf(branch)+1)
            .Take(damage.Body.Instructions.IndexOf(target)-damage.Body.Instructions.IndexOf(branch)-1);
        if(!range.Any(i=>i.Operand is MethodReference m && m.Name=="OverlapBox") || !range.Any(i=>i.Operand is MethodReference m && m.Name=="TakeDamage"))
            throw new Exception("Quench branch does not skip the damaging overlap");
        Console.WriteLine("PASS: actual RoR2 DamageTrail IL patched; quenched section skips overlap/damage and advances to the next section without bridging gaps.");
    }
}
