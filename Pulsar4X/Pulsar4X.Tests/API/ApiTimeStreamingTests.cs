using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Pulsar4X.Api;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Aurora-style display: the client map/HUD follow the global tick clock. Sub-step system
    /// clocks must not flood TimeChanged during a long pulse — that made week/month ticks look
    /// equally "smooth" instead of larger spatial jumps per Ticklength.
    /// </summary>
    [TestFixture]
    public class ApiTimeStreamingTests : ApiTestBase
    {
        [Test]
        public void Long_pulse_does_not_stream_substep_system_clock_to_clients()
        {
            _game.Settings.EnforceSingleThread = false;
            var session = Connect();
            _server.SetSystemFocus(session, _game.Systems[0].ID);

            var systemTimes = new ConcurrentQueue<DateTime>();
            using var sub = _server.Subscribe(session, e =>
            {
                if (e.Type == GameEventType.TimeChanged && e.Time != null && e.SystemId != null)
                    systemTimes.Enqueue(e.Time.GameDateTime);
            });

            var start = _game.TimePulse.GameGlobalDateTime;
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.SetTickLength, TickLength: TimeSpan.FromDays(30)));
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.SetTickFrequency, TickFrequency: TimeSpan.FromMilliseconds(10)));
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Start));
            Thread.Sleep(800);
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Pause));
            for (int i = 0; i < 100 && _game.TimePulse.IsRunning; i++) Thread.Sleep(20);
            Thread.Sleep(100);

            var distinctSystem = systemTimes.Distinct().Where(t => t > start).OrderBy(t => t).ToList();
            TestContext.WriteLine($"distinct systemId TimeChanged after start = {distinctSystem.Count}");
            // Global ticks may sync focused system DateTime once per Ticklength — not hundreds of sub-steps.
            Assert.That(distinctSystem.Count, Is.LessThan(20),
                "system-scoped TimeChanged must not stream every hotloop sub-step during a long pulse");
        }

        [Test]
        public void Global_clock_advances_in_tick_length_sized_jumps()
        {
            _game.Settings.EnforceSingleThread = false;
            var session = Connect();
            _server.SetSystemFocus(session, _game.Systems[0].ID);

            var globalTimes = new ConcurrentQueue<DateTime>();
            using var sub = _server.Subscribe(session, e =>
            {
                if (e.Type == GameEventType.TimeChanged && e.Time != null && e.SystemId == null)
                    globalTimes.Enqueue(e.Time.GameDateTime);
            });

            var start = _game.TimePulse.GameGlobalDateTime;
            var tick = TimeSpan.FromDays(7);
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.SetTickLength, TickLength: tick));
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.SetTickFrequency, TickFrequency: TimeSpan.FromMilliseconds(10)));
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Start));
            Thread.Sleep(600);
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Pause));
            for (int i = 0; i < 100 && _game.TimePulse.IsRunning; i++) Thread.Sleep(20);
            Thread.Sleep(100);

            var advances = globalTimes.Distinct().Where(t => t > start).OrderBy(t => t).ToList();
            Assert.That(advances.Count, Is.GreaterThan(0), "expected at least one global tick advance");

            // First completed tick should land near start+Ticklength (allow small interrupt skew).
            var firstJump = advances.First() - start;
            TestContext.WriteLine($"first global jump = {firstJump.TotalDays:0.##} days (TickLength=7)");
            Assert.That(firstJump.TotalDays, Is.GreaterThan(6.0).And.LessThan(8.5),
                "first global clock jump should be about one Ticklength (week), not a 5-minute sub-step");
        }

        [Test]
        public void Pausing_pushes_a_final_stopped_clock_so_controls_unlock()
        {
            _game.Settings.EnforceSingleThread = false;
            var session = Connect();
            _server.SetSystemFocus(session, _game.Systems[0].ID);

            var states = new ConcurrentQueue<TimeState>();
            using var sub = _server.Subscribe(session, e =>
            {
                if (e.Type == GameEventType.TimeChanged && e.Time != null) states.Enqueue(e.Time);
            });

            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.SetTickFrequency, TickFrequency: TimeSpan.FromMilliseconds(10)));
            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Start));
            Thread.Sleep(300);
            Assert.That(_game.TimePulse.IsRunning, Is.True, "should be running before pause");

            _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Pause));

            for (int i = 0; i < 100 && _game.TimePulse.IsRunning; i++) Thread.Sleep(20);
            Assert.That(_game.TimePulse.IsRunning, Is.False, "the clock should have stopped after pause");
            Thread.Sleep(100);

            Assert.That(states.IsEmpty, Is.False);
            var last = states.Last();
            Assert.That(last.IsRunning, Is.False, "final pushed clock must be IsRunning=false so the UI unlocks");
            Assert.That(last.IsStopping, Is.False, "final pushed clock must clear IsStopping");
        }
    }
}
