using System;
using System.Threading;
using NUnit.Framework;
using Pulsar4X.Api;

namespace Pulsar4X.Tests;

[TestFixture]
public class ApiTickLengthAdvanceTests : ApiTestBase
{
    TimeSpan RunAndMeasureAdvance(TimeSpan tickLength)
    {
        var session = Connect();
        _server.SetSystemFocus(session, _game.Systems[0].ID);

        var start = _game.TimePulse.GameGlobalDateTime;
        _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Pause));
        _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.SetTickLength, TickLength: tickLength));
        _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.SetTickFrequency, TickFrequency: TimeSpan.FromMilliseconds(50)));
        _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Start));
        Thread.Sleep(600);
        _server.SetTimeControl(session, new TimeControlRequest(TimeControlAction.Pause));
        for (int i = 0; i < 80 && _game.TimePulse.IsRunning; i++)
            Thread.Sleep(25);

        return _game.TimePulse.GameGlobalDateTime - start;
    }

    [Test]
    public void Longer_tick_length_advances_more_game_time_per_real_second()
    {
        _game.Settings.EnforceSingleThread = false;

        var shortAdvance = RunAndMeasureAdvance(TimeSpan.FromHours(1));
        var longAdvance = RunAndMeasureAdvance(TimeSpan.FromDays(30));

        TestContext.WriteLine($"1h tick: advanced {shortAdvance.TotalDays:0.###} days");
        TestContext.WriteLine($"30d tick: advanced {longAdvance.TotalDays:0.###} days");

        Assert.That(longAdvance, Is.GreaterThan(shortAdvance * 10),
            "30-day ticks should advance far more game time than 1-hour ticks in the same wall interval");
    }
}
