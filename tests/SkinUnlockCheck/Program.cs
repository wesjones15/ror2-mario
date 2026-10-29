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
        luigi.Unlock = new UnlockableDef(); piantissimo.Unlock = new UnlockableDef(); yoshi.Unlock = new UnlockableDef();
        Check(luigi.UnlockCoins == 2401 && luigi.UnlockStage == 0 && luigi.UnlockHint == "L is real 2401", "Luigi's released criterion changed");
        Check(piantissimo.UnlockStage == 4 && piantissimo.UnlockCoins == 0, "Il Piantissimo needs the stage-four key");
        Check(piantissimo.Text == "Il Piantissimo", "Il Piantissimo's name is spelled out");
        Check(yoshi.UnlockStage == 5 && yoshi.UnlockCoins == 0, "Yoshi needs the stage-five key");
        var body = new CharacterBody { master = new CharacterMaster(), healthComponent = new HealthComponent { alive = true } };
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
        Check(profile.HasUnlockable(yoshi.Unlock), "The stage-five teleporter must hatch Yoshi");
        Check(profile.Saves == 2, "Yoshi's grant must save once");
        Console.WriteLine("PASS: Luigi 2401 held coins; Il Piantissimo living stage-four teleporter; Yoshi stage five; independent persistent grants.");
    }
}
