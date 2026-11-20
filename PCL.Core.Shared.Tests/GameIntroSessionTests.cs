using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class GameIntroSessionTests
{
    [TestMethod]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow("1", true)]
    [DataRow("0", false)]
    public void IntroIsEnabledUnlessExplicitlyDisabled(string? setting, bool expected)
    {
        Assert.AreEqual(expected, GameIntroLaunch.IsEnabled(setting));
    }

    [TestMethod]
    public void WaitsForRealReadinessAndRequiresANewPressAfterPrompt()
    {
        var session = new GameIntroSession();
        var beforeReady = session.Apply(GameIntroEvent.SpaceDown, TimeSpan.FromMinutes(2));
        Assert.AreEqual(GameIntroPhase.Loading, beforeReady.Phase);
        Assert.IsTrue(beforeReady.PlayIntroAudio);
        Assert.AreEqual(0d, beforeReady.PromptOpacity);

        var readyAt = TimeSpan.FromMinutes(3);
        Assert.AreEqual(0d, session.Apply(GameIntroEvent.GameReady, readyAt).PromptOpacity);
        Assert.AreEqual(0.5d, session.Apply(GameIntroEvent.GameReady, readyAt.Add(TimeSpan.FromMilliseconds(150))).PromptOpacity);
        var promptAt = readyAt.Add(TimeSpan.FromMilliseconds(300));
        Assert.AreEqual(GameIntroPhase.AwaitingSpace, session.Apply(GameIntroEvent.Tick, promptAt).Phase);
        Assert.AreEqual(GameIntroPhase.AwaitingSpace, session.Apply(GameIntroEvent.SpaceDown, promptAt).Phase);
        session.Apply(GameIntroEvent.SpaceUp, promptAt);
        Assert.AreEqual(GameIntroPhase.FadingOut, session.Apply(GameIntroEvent.SpaceDown, promptAt).Phase);
    }

    [TestMethod]
    public void AcceptsFreshSpaceAfterPromptEvenWithoutAnInterveningRenderTick()
    {
        var session = new GameIntroSession();
        session.Apply(GameIntroEvent.GameReady, TimeSpan.Zero);
        var frame = session.Apply(GameIntroEvent.SpaceDown, TimeSpan.FromMilliseconds(301));
        Assert.AreEqual(GameIntroPhase.FadingOut, frame.Phase);
        Assert.IsTrue(frame.PlayIntroAudio);
        Assert.IsTrue(frame.HoldGameAudioAndInput);
    }

    [TestMethod]
    public void KeepsIntroAudibleThroughFadeAndHoldsGameUntilAudioActuallyStops()
    {
        var session = new GameIntroSession();
        session.Apply(GameIntroEvent.GameReady, TimeSpan.Zero);
        var pressedAt = TimeSpan.FromMilliseconds(300);
        session.Apply(GameIntroEvent.Tick, pressedAt);
        session.Apply(GameIntroEvent.SpaceDown, pressedAt);
        var fade = session.Apply(GameIntroEvent.Tick, TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(0.5d, fade.OverlayOpacity);
        Assert.IsTrue(fade.PlayIntroAudio);
        Assert.IsTrue(fade.HoldGameAudioAndInput);
        var stopping = session.Apply(GameIntroEvent.Tick, TimeSpan.FromMilliseconds(700));
        Assert.AreEqual(0d, stopping.OverlayOpacity);
        Assert.IsFalse(stopping.PlayIntroAudio);
        Assert.IsTrue(stopping.HoldGameAudioAndInput);
        var released = session.Apply(GameIntroEvent.AudioStopped, TimeSpan.FromMilliseconds(710));
        Assert.AreEqual(GameIntroPhase.Released, released.Phase);
        Assert.IsFalse(released.HoldGameAudioAndInput);
    }

    [TestMethod]
    public void FailureStopsPlaybackAndLateEventsCannotRestartTheIntro()
    {
        var session = new GameIntroSession();
        var failed = session.Apply(GameIntroEvent.Failure, TimeSpan.Zero);
        Assert.AreEqual(GameIntroPhase.StoppingAudio, failed.Phase);
        Assert.AreEqual(0d, failed.OverlayOpacity);
        Assert.IsFalse(failed.PlayIntroAudio);
        Assert.IsTrue(failed.HoldGameAudioAndInput);
        session.Apply(GameIntroEvent.AudioStopped, TimeSpan.Zero);
        var late = session.Apply(GameIntroEvent.GameReady, TimeSpan.FromMinutes(1));
        Assert.AreEqual(GameIntroPhase.Released, late.Phase);
        Assert.IsFalse(late.HoldGameAudioAndInput);
    }
}
