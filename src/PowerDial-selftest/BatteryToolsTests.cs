using System;
using System.Collections.Generic;
using System.Text.Json;
using PowerDial;

sealed partial class SelfTest
{
    static void BatteryToolsChecks()
    {
        Console.WriteLine("--- alerts, automation, undo and battery sessions ---");
        BatteryToolsOptions options = new BatteryToolsOptions();
        BatteryToolsState state = new BatteryToolsState();
        BatteryToolsPolicy policy = new BatteryToolsPolicy();
        BatteryObservation sample = new BatteryObservation { T = 1000, Valid = true, Percent = 20, Watts = 10 };
        Check("automation is opt-in", !options.AutoEnabled && policy.Evaluate(options, state, sample, null).Mode == 0);
        Check("low charge alert fires once", state.LowSent && policy.Evaluate(options, state, sample, null).Alert == null);
        sample.Percent = 22; sample.T += 15;
        policy.Evaluate(options, state, sample, null);
        Check("low alert hysteresis avoids threshold chatter", state.LowSent);
        sample.Percent = 26; sample.T += 15;
        policy.Evaluate(options, state, sample, null);
        sample.Percent = 20; sample.T += 15;
        Check("low alert re-arms above threshold plus five", policy.Evaluate(options, state, sample, null).Alert != null);
        state.LowSent = false; state.SnoozeUntil = sample.T + 3600;
        Check("snooze suppresses notifications without consuming the alert", policy.Evaluate(options, state, sample, null).Alert == null && !state.LowSent);
        sample.T += 3601;
        Check("alerts resume after snooze", policy.Evaluate(options, state, sample, null).Alert != null);

        options.ChargeAlert = true; sample.Ac = true; sample.Percent = 80; sample.T += 15;
        Check("charge target reminder fires on AC", policy.Evaluate(options, state, sample, null).Alert != null);
        sample.T += 15;
        Check("charge target reminder does not repeat every poll", policy.Evaluate(options, state, sample, null).Alert == null);
        sample.Ac = false; sample.T += 15;
        Check("charge reminder never fires unplugged", policy.Evaluate(options, state, sample, null).Alert == null && !state.ChargeSent);

        options.AutoEnabled = true; sample.Percent = 25; sample.T += 15;
        Check("automation fires at the configured threshold", policy.Evaluate(options, state, sample, null).Mode == 2);
        sample.T += 15;
        Check("automation attempts only once per discharge crossing", policy.Evaluate(options, state, sample, null).Mode == 0);
        sample.Ac = true; sample.T += 15;
        Check("automation does not run on AC and re-arms", policy.Evaluate(options, state, sample, null).Mode == 0 && !state.AutoHandled);
        sample.Ac = false; state.PauseUntil = sample.T + 3600; sample.T += 15;
        Check("manual pause suppresses automatic changes", policy.Evaluate(options, state, sample, null).Mode == 0);
        sample.T += 3600;
        Check("automation resumes after the pause", policy.Evaluate(options, state, sample, null).Mode == 2);
        state.AutoHandled = false; sample.Valid = false; sample.T += 15;
        Check("invalid battery telemetry causes no automation or alert", policy.Evaluate(options, state, sample, null).Mode == 0 && !state.AutoHandled);
        sample.Valid = true; sample.Percent = 101;
        Check("invalid percentage cannot trigger automation", policy.Evaluate(options, state, sample, null).Mode == 0);

        options.AutoEnabled = false; options.LowAlert = false; options.ChargeAlert = false;
        sample.Percent = 50; sample.Ac = false; sample.Watts = 25; sample.T = 10000;
        policy = new BatteryToolsPolicy(); state = new BatteryToolsState();
        int alerts = 0;
        for (int i = 0; i <= 20; i++)
        {
            sample.T = 10000 + i * 15;
            if (policy.Evaluate(options, state, sample, 10).Alert != null) alerts++;
            if (i == 19) Check("a short drain spike is not an alert", alerts == 0);
        }
        Check("five minutes of sustained high drain raises one alert", alerts == 1);
        sample.T += 15;
        Check("drain alert cooldown prevents spam", policy.Evaluate(options, state, sample, 10).Alert == null);
        sample.T += 3600;
        Check("a sleep gap resets sustained-drain timing", policy.Evaluate(options, state, sample, 10).Alert == null);
        policy.ResetWindow(); sample.T += 15;
        Check("no measured baseline means no high-drain guess", policy.Evaluate(options, state, sample, null).Alert == null);

        List<HistPoint> prior = new List<HistPoint>();
        for (int i = 0; i < 20; i++) prior.Add(new HistPoint { T = 1000 + i * 60, W = 10 });
        prior.Add(new HistPoint { T = 3600, W = 1000 });
        Check("drain baseline excludes the current spike", BatteryToolsPolicy.Baseline(prior, 3600) == 10);
        Check("old baselines expire", BatteryToolsPolicy.Baseline(prior, 3600 + 8 * 86400) == null);
        Check("insufficient baseline is unavailable", BatteryToolsPolicy.Baseline(new List<HistPoint>(), 3600) == null);

        Config config = new Config { BatteryTools = options, BatteryState = state };
        config.BatteryTools.AutoEnabled = true;
        config.BatteryState.PauseUntil = 12345;
        config.BatteryState.Undo = new AutomaticChange { Scheme = "381b4222-f694-41f0-9685-ff5bb260df2e" };
        config.BatteryState.Undo.Before["epp"] = 80;
        config.BatteryState.Undo.Applied["epp"] = 90;
        Config restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, PrettyJson.Default.Config), PrettyJson.Default.Config);
        Check("options, pause and undo survive restart serialization", restored.BatteryTools.AutoEnabled &&
            restored.BatteryState.PauseUntil == 12345 && restored.BatteryState.Undo.Before["epp"] == 80);
        Config old = JsonSerializer.Deserialize("{\"Version\":1}", PrettyJson.Default.Config);
        Check("older configs keep automation off and get alert defaults", !old.BatteryTools.AutoEnabled && old.BatteryTools.LowPercent == 20);
        GuardedWriteChecks();
        SessionChecks();
    }

    static void GuardedWriteChecks()
    {
        const string scheme = "381b4222-f694-41f0-9685-ff5bb260df2e";
        int current = 80, writes = 0;
        Func<int, string> write = delegate (int value) { current = value; writes++; return null; };
        Check("automatic writes read back the captured plan", AutomationWrites.Guarded(scheme, 80, 90, () => scheme, () => current, write) == null && writes == 1 && current == 90);
        Check("undo restores the previous value", AutomationWrites.Guarded(scheme, 90, 80, () => scheme, () => current, write) == null && current == 80);
        int before = writes;
        Check("changed plan prevents writes", AutomationWrites.Guarded(scheme, 80, 90, () => Guid.Empty.ToString(), () => current, write) != null && writes == before);
        Check("later manual edits are preserved", AutomationWrites.Guarded(scheme, 90, 80, () => scheme, () => current, write) != null && writes == before);
        Check("unreadable setting prevents writes", AutomationWrites.Guarded(scheme, 80, 90, () => scheme, () => null, write) != null && writes == before);
        Check("permission failures are returned", AutomationWrites.Guarded(scheme, 80, 90, () => scheme, () => current, value => "denied") == "denied");
        Check("write success without read-back is not success", AutomationWrites.Guarded(scheme, 80, 90, () => scheme, () => current, value => null) != null);
    }

    static void SessionChecks()
    {
        List<HistPoint> points = new List<HistPoint> {
            new HistPoint { T = 1000, Pct = 80, E = 40000, W = 10, M = 1, S = "a", Top = "Browser Helper 20.0" },
            new HistPoint { T = 1060, Pct = 79, E = 39800, W = 12, M = 2, S = "a", Top = "Browser Helper 20.0" },
            new HistPoint { T = 1120, Pct = 78, E = 39600, W = 14, M = 2, S = "a", Top = "Code 10.0" }
        };
        List<SessionSummary> sessions = SessionSummaries.From(points, 1130, "a");
        Check("mode switches remain within a battery session", sessions.Count == 1 && sessions[0].Samples == 3);
        Check("session duration excludes the unobserved first minute", sessions[0].End - sessions[0].Start == 120);
        Check("session energy comes from the battery counter", Math.Abs(sessions[0].EnergyWh.Value - 0.4) < 0.001);
        Check("session average is weighted by recorded intervals", sessions[0].AverageWatts == 13);
        Check("session labels CPU activity without assigning app energy", sessions[0].Busiest == "Browser Helper");
        Check("current recorded segment is marked in progress", sessions[0].Status == "In progress");
        points.Add(new HistPoint { T = 1180, Ac = true, Pct = 78 });
        Check("plugging in closes a session", SessionSummaries.From(points, 1190, null)[0].Status == "Plugged in");
        points.Add(new HistPoint { T = 1240, Pct = 78, E = 39600, S = "b" });
        Check("unplugging begins another recorded segment", SessionSummaries.From(points, 1250, "b").Count == 2);
        points.Add(new HistPoint { T = 1300, Pct = 77, E = 39500, S = "c" });
        Check("app restarts split sessions even without a large gap", SessionSummaries.From(points, 1310, "c").Count == 3);
        points.Add(new HistPoint { T = 5000, Pct = 70, E = 35000, S = "c" });
        Check("sleep gaps split sessions rather than inflate duration", SessionSummaries.From(points, 5010, "c").Count == 4);
        points[0].E = null;
        Check("legacy records do not invent energy measurements", SessionSummaries.From(points, 5010, "c")[3].EnergyWh == null);
        points[0].E = 39000;
        Check("capacity increases do not produce negative energy usage", SessionSummaries.From(points, 5010, "c")[3].EnergyWh == null);
        Check("empty history has no sessions", SessionSummaries.From(new List<HistPoint>(), 1000, null).Count == 0);
    }
}
