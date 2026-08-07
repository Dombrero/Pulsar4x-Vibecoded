using NUnit.Framework;

namespace Pulsar4X.Tests;

[TestFixture]
public class EngineCommandInboxTests : ApiTestBase
{
    [Test]
    public void Game_has_command_inbox_and_drain_is_safe_when_idle()
    {
        Connect();
        Assert.That(_game.CommandInbox, Is.Not.Null);
        Assert.DoesNotThrow(() => _game.CommandInbox.Drain(_game));
    }
}
