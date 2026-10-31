using System;
using System.Reflection;
using MarioTest;
using RoR2;
using UnityEngine;

// Executes the real MarioCoins.FixedUpdate with a minimal host, no game/native assets.
static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static Array Coins(MarioCoins coins) => (Array)typeof(MarioCoins).GetField("coins", Private).GetValue(coins);
    static void Put(MarioCoins coins, int index, Vector3 pos, float age = 1f)
    {
        var slots = Coins(coins);
        var coin = Activator.CreateInstance(slots.GetType().GetElementType(), true);
        var type = coin.GetType();
        type.GetField("pos").SetValue(coin, pos);
        type.GetField("age").SetValue(coin, age);
        type.GetField("team").SetValue(coin, TeamIndex.Player);
        type.GetField("settled").SetValue(coin, true);
        slots.SetValue(coin, index);
    }
    static void Step(MarioCoins coins) => typeof(MarioCoins).GetMethod("FixedUpdate", Private).Invoke(coins, null);
    static CharacterBody Body(bool mario, TeamIndex team = TeamIndex.Player)
    {
        var body = new CharacterBody { mario = mario ? new MarioDriver() : null,
            teamComponent = new TeamComponent { teamIndex = team },
            healthComponent = new HealthComponent(), master = new CharacterMaster() };
        CharacterBody.readOnlyInstancesList.Add(body);
        return body;
    }
    // The canned path: pops up, comes down on the floor, bounces lower each time, lies still.
    static void Bounces()
    {
        var start = new Vector3(0, 2, 0);
        Check(CoinBounce.Make(start, new Vector3(1, 7, 0), 0.11f, out var path), "a path over the floor");
        Check(path.flights >= 2 && path.flights <= 6, $"a few bounces ({path.flights})");
        float peak = 0f, lastHigh = float.MaxValue, high = 0f;
        int was = 0;
        for (float t = 0f; t < path.Duration + 1f; t += 1f / 120f)
        {
            var at = path.At(t, out int landed);
            Check(at.y >= 0.11f - 1e-4f, $"never under the floor ({at.y} at {t})");
            if (landed == 0) peak = Math.Max(peak, at.y);
            if (landed != was)
            {
                if (was > 0) { Check(high < lastHigh, "each bounce lower"); lastHigh = high; }
                high = 0f;
                was = landed;
            }
            if (landed > 0) high = Math.Max(high, at.y);
        }
        Check(peak > 2.9f && peak < 3.1f, $"pops about 1 m up ({peak})");
        var rest = path.At(path.Duration + 5f, out int all);
        Check(all == path.flights && Math.Abs(rest.y - 0.11f) < 1e-4f, "lies on the floor at the end");
        var side = path.Landing();
        Check(Math.Abs(rest.x - side.x) < 1e-3f && side.x > 0f, "drifts to where Landing says");
        // The coin uses it when there is a floor, and falls the old way when there is none.
        Physics.floor = 0f;
        var coins = new MarioCoins();
        var plan = typeof(MarioCoins).GetMethod("Plan", BindingFlags.Static | BindingFlags.NonPublic);
        var slots = Coins(coins);
        var coin = Activator.CreateInstance(slots.GetType().GetElementType(), true);
        coin.GetType().GetField("pos").SetValue(coin, start);
        coin.GetType().GetField("velocity").SetValue(coin, new Vector3(1, 7, 0));
        plan.Invoke(null, new[] { coin });
        Check((bool)coin.GetType().GetField("planned").GetValue(coin), "planned over a floor");
        Physics.floor = null;
        plan.Invoke(null, new[] { coin });
        Check(!(bool)coin.GetType().GetField("planned").GetValue(coin), "no floor, no plan");
    }

    static void Main()
    {
        Run.instance = new Run();
        UnityEngine.Networking.NetworkServer.active = true;
        for (int i = 0; i < 200; i++) Body(false, TeamIndex.Monster);
        var mario = Body(true);
        mario.footPosition = new Vector3(100, 0, 0);
        var coins = new MarioCoins();
        for (int i = 0; i < 100; i++) Put(coins, i, Vector3.zero);
        CharacterBody.lookups = 0;
        CharacterBody.readOnlyInstancesList.visits = 0;
        Step(coins);
        Check(CharacterBody.lookups == 301, "one 201-body scan plus 100 Mario checks");
        Check(CharacterBody.readOnlyInstancesList.visits == 201, "stage body enumeration happens once");
        Check(mario.healthComponent.heals == 0, "distant coins stay put");
        Console.WriteLine("Dense stage: 201 stage-body visits + 100 collector checks for 100 coins / 201 bodies (previous loop: 20,100 body visits).");

        mario.footPosition = Vector3.zero;
        mario.healthComponent.onHeal = () => Check(Coins(coins).GetValue(mario.healthComponent.heals - 1) == null, "remove before healing callbacks");
        Step(coins);
        Check(mario.healthComponent.heals == 100 && mario.healthComponent.healed == 1000f, "each coin heals one eighth exactly once");
        Check(mario.master.money == 300 && MarioNet.taken == 100 && MarioNet.collected == 100, "gold and network notifications preserved");
        Step(coins);
        Check(mario.healthComponent.heals == 100, "no double healing");

        CharacterBody.readOnlyInstancesList.Clear();
        var wrongTeam = Body(true, TeamIndex.Monster);
        var dead = Body(true); dead.healthComponent.alive = false;
        var first = Body(true);
        var clone = Body(true);
        coins = new MarioCoins();
        Put(coins, 0, Vector3.zero); Put(coins, 1, Vector3.zero);
        first.healthComponent.onHeal = () => first.healthComponent.alive = false;
        Step(coins);
        Check(first.healthComponent.heals == 1 && clone.healthComponent.heals == 1, "body order and AI Marios retained; recheck alive after callbacks");
        Check(wrongTeam.healthComponent.heals == 0 && dead.healthComponent.heals == 0, "team and life filters retained");

        coins = new MarioCoins();
        Put(coins, 0, Vector3.zero, 0f);
        CharacterBody.lookups = 0;
        Step(coins);
        Check(Coins(coins).GetValue(0) != null && CharacterBody.lookups == 0, "pickup delay avoids scans");
        Put(coins, 0, Vector3.zero, 60f);
        Step(coins);
        Check(Coins(coins).GetValue(0) == null && CharacterBody.lookups == 0, "expiry retained");

        Put(coins, 0, Vector3.zero);
        UnityEngine.Networking.NetworkServer.active = false;
        Step(coins);
        Check(Coins(coins).GetValue(0) != null && CharacterBody.lookups == 0, "clients never collect or scan bodies");
        // A client's own Mario on a coin asks the server for it (its copy of the coin stays until the
        // server says "taken"); it asks again only after half a second.
        CharacterBody.readOnlyInstancesList.Clear();
        var own = Body(true); own.hasEffectiveAuthority = true; own.footPosition = Vector3.zero;
        var other = Body(true); other.footPosition = Vector3.zero;
        Put(coins, 0, Vector3.zero);
        MarioNet.touches = 0;
        Step(coins);
        Check(MarioNet.touches == 1 && Coins(coins).GetValue(0) != null, "a client reports its own Mario's touch and keeps the coin");
        Step(coins);
        Check(MarioNet.touches == 1, "the touch is not resent every step");
        own.footPosition = new Vector3(100, 0, 0);
        Put(coins, 1, Vector3.zero);
        Step(coins);
        Check(MarioNet.touches == 1, "a Mario out of reach reports nothing");
        // The server takes the report for any Mario of the coin's team, wherever it thinks his body is.
        UnityEngine.Networking.NetworkServer.active = true;
        var sender = Body(true); sender.footPosition = new Vector3(500, 0, 500);
        MarioNet.taken = 0;
        coins.Touched(0, sender);
        Check(Coins(coins).GetValue(0) == null && MarioNet.taken == 1 && sender.healthComponent.heals == 1, "the server collects a touched coin for a client's Mario");
        coins.Touched(0, sender);
        Check(MarioNet.taken == 1, "a coin is taken once");
        var foe = Body(true, TeamIndex.Monster);
        coins.Touched(1, foe);
        Check(Coins(coins).GetValue(1) != null, "another team's Mario cannot take it");
        UnityEngine.Networking.NetworkServer.active = true;
        Put(coins, 0, Vector3.zero);
        CharacterBody.lookups = 0;
        PauseManager.isPaused = true;
        Step(coins);
        Check(Coins(coins).GetValue(0) != null && CharacterBody.lookups == 0, "pause retained");
        Bounces();
        Console.WriteLine("CoinCheck: ok");
    }
}
