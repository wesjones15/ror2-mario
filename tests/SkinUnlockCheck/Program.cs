using System;
using MarioTest;
using RoR2;

static class Program
{
    static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    static void Main()
    {
        var luigi = MarioSkin.Spec(MarioSkin.Luigi);
        var piantissimo = MarioSkin.Spec(MarioSkin.Piantissimo);
        var yoshi = MarioSkin.Spec(MarioSkin.Yoshi);
        Ror2Common.ModPresence.Melee=false;
        MarioSkin.Register(new UnityEngine.GameObject(),new RoR2.ContentManagement.ContentPack());
        Check(!luigi.Registered,"Luigi appeared without the Melee mod");
        Ror2Common.ModPresence.Melee=true;
        MarioSkin.Register(new UnityEngine.GameObject(), new RoR2.ContentManagement.ContentPack());
        Check(luigi.Registered && luigi.Def && luigi.Unlock && luigi.Def.unlockableDef == luigi.Unlock,
            "missing Melee files hid Luigi or removed his persistent unlock");
        Check(MarioSkin.UsesStandIn(MarioSkin.Luigi), "missing Melee files did not select Luigi's stand-in");
        Check(yoshi.Unlock && yoshi.Def.unlockableDef == yoshi.Unlock && yoshi.Unlock.cachedName == "Skins.Mario.Yoshi", "Register Yoshi's persistent Mithrix key on his skin");
        Check(luigi.UnlockCoins == 2401 && luigi.UnlockStage == 0 && luigi.UnlockHint == "L is real 2401", "Luigi's released criterion changed");
        Check(piantissimo.UnlockStage == 4 && piantissimo.UnlockCoins == 0, "Il Piantissimo needs the stage-four key");
        Check(piantissimo.Text == "Il Piantissimo", "Il Piantissimo's name is spelled out");
        Check(yoshi.UnlockMithrix && yoshi.UnlockStage == 0 && yoshi.UnlockCoins == 0, "Yoshi needs the Mithrix key only");
        var body = new CharacterBody { baseNameToken="MARIOTEST_BODY_NAME", master = new CharacterMaster(), healthComponent = new HealthComponent { alive = true } };
        var profile = new UserProfile();
        LocalUserManager.readOnlyLocalUsersList.Add(new LocalUser { cachedBody = body, userProfile = profile });
        Run.instance = new Run();
        body.master.money = 2400;
        MarioSkin.CheckUnlocks(body);
        Check(!profile.HasUnlockable(luigi.Unlock), "2400 coins unlocked Luigi");
        body.master.money = 2401;
        MarioSkin.CheckUnlocks(body);
        Check(profile.HasUnlockable(luigi.Unlock) && !profile.HasUnlockable(piantissimo.Unlock), "Coin key leaked into Piantissimo");
        body.master.money = 0;
        MarioSkin.CheckUnlocks(body);
        Check(profile.HasUnlockable(luigi.Unlock), "Spending coins relocked Luigi");
        profile = new UserProfile(); LocalUserManager.readOnlyLocalUsersList[0].userProfile = profile;
        TeleporterInteraction.instance = new TeleporterInteraction { activationState = 3 };
        Run.instance.stageClearCount = 2;
        MarioSkin.CheckUnlocks(body);
        Check(!profile.HasUnlockable(piantissimo.Unlock), "Stage three unlocked Piantissimo");
        Run.instance.stageClearCount = 3;
        TeleporterInteraction.instance.activationState = 2;
        MarioSkin.CheckUnlocks(body);
        Check(!profile.HasUnlockable(piantissimo.Unlock), "Incomplete teleporter unlocked Piantissimo");
        TeleporterInteraction.instance.activationState = 3;
        body.healthComponent.alive = false;
        MarioSkin.CheckUnlocks(body);
        Check(!profile.HasUnlockable(piantissimo.Unlock), "Dead Mario unlocked Piantissimo");
        body.healthComponent.alive = true;
        MarioSkin.CheckUnlocks(body); MarioSkin.CheckUnlocks(body);
        Check(profile.HasUnlockable(piantissimo.Unlock) && !profile.HasUnlockable(luigi.Unlock), "Stage-four key leaked into Luigi");
        Run.instance.stageClearCount = 4;
        MarioSkin.CheckUnlocks(body);
        Check(!profile.HasUnlockable(yoshi.Unlock), "Stage five must not unlock Yoshi");
        var other = new UserProfile();
        LocalUserManager.readOnlyLocalUsersList.Add(new LocalUser { cachedBody = new CharacterBody { baseNameToken="OLIMARTEST_BODY_NAME" }, userProfile=other });
        var deadMario = new UserProfile();
        LocalUserManager.readOnlyLocalUsersList.Add(new LocalUser { cachedMaster = new CharacterMaster { bodyPrefab = new UnityEngine.GameObject { Component = new CharacterBody { baseNameToken="MARIOTEST_BODY_NAME" } } }, userProfile=deadMario });
        MarioSkin.OnCharacterDeath(new DamageReport { victimBody = new CharacterBody { bodyIndex=BodyIndex.Brother } });
        Check(!profile.HasUnlockable(yoshi.Unlock) && MarioNet.MithrixMessages == 0, "An earlier Mithrix phase unlocked Yoshi");
        MarioSkin.MithrixDefeated("old-run");
        Check(!profile.HasUnlockable(yoshi.Unlock), "A stale network message unlocked Yoshi");
        UnityEngine.Networking.NetworkServer.active=false;
        MarioSkin.OnCharacterDeath(new DamageReport { victimBody = new CharacterBody { bodyIndex=BodyIndex.BrotherHurt } });
        Check(!profile.HasUnlockable(yoshi.Unlock), "A client death event bypassed server authority");
        UnityEngine.Networking.NetworkServer.active=true;
        MarioSkin.OnCharacterDeath(new DamageReport { victimBody = new CharacterBody { bodyIndex=BodyIndex.BrotherHurt } });
        MarioSkin.MithrixDefeated(Run.instance.GetUniqueId());
        Check(profile.HasUnlockable(yoshi.Unlock) && deadMario.HasUnlockable(yoshi.Unlock), "Defeating final Mithrix must reward living and dead Mario players");
        Check(!other.HasUnlockable(yoshi.Unlock), "Another survivor unlocked Mario's Yoshi");
        Check(profile.Saves == 2 && deadMario.Saves == 1 && MarioNet.MithrixMessages == 1, "Mithrix grant must save once and notify clients");
        profile = new UserProfile(); LocalUserManager.readOnlyLocalUsersList[0].userProfile=profile;
        UnityEngine.Networking.NetworkServer.active=false;
        MarioSkin.MithrixDefeated(Run.instance.GetUniqueId());
        Check(profile.HasUnlockable(yoshi.Unlock) && profile.Saves == 1, "A remote client's profile must earn the server's Mithrix reward");
        Console.WriteLine("PASS: independent persistent Mario skins; Yoshi final Mithrix only, co-op profiles, dead players, stale messages and server authority.");
    }
}
