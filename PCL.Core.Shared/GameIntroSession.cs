namespace PCL.Core.Shared.Minecraft;

public enum GameIntroEvent
{
    Tick,
    GameReady,
    SpaceDown,
    SpaceUp,
    Failure,
    AudioStopped
}

public enum GameIntroPhase
{
    Loading,
    ShowingPrompt,
    AwaitingSpace,
    FadingOut,
    StoppingAudio,
    Released
}

public readonly record struct GameIntroPresentation(
    GameIntroPhase Phase,
    double OverlayOpacity,
    double PromptOpacity)
{
    public bool PlayIntroAudio => Phase is not (GameIntroPhase.StoppingAudio or GameIntroPhase.Released);
    public bool HoldGameAudioAndInput => Phase != GameIntroPhase.Released;
}

/// <summary>
/// Controls presentation only. The host serializes events with a monotonic play-session clock
/// and reports GameReady only after vanilla can present its first interactive frame.
/// </summary>
public sealed class GameIntroSession
{
    private static readonly TimeSpan PromptDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(400);
    private GameIntroPhase _phase;
    private TimeSpan _phaseStarted;
    private TimeSpan _lastTime;
    private bool _spaceDown;

    public GameIntroPresentation Apply(GameIntroEvent input, TimeSpan elapsed)
    {
        elapsed = elapsed < _lastTime ? _lastTime : elapsed;
        _lastTime = elapsed;
        var freshSpace = input == GameIntroEvent.SpaceDown && !_spaceDown;
        if (input == GameIntroEvent.SpaceDown) _spaceDown = true;
        if (input == GameIntroEvent.SpaceUp) _spaceDown = false;

        if (_phase != GameIntroPhase.Released && input == GameIntroEvent.Failure)
            SetPhase(GameIntroPhase.StoppingAudio, elapsed);
        else if (_phase == GameIntroPhase.StoppingAudio && input == GameIntroEvent.AudioStopped)
            SetPhase(GameIntroPhase.Released, elapsed);
        else if (_phase == GameIntroPhase.Loading && input == GameIntroEvent.GameReady)
            SetPhase(GameIntroPhase.ShowingPrompt, elapsed);
        else if (_phase == GameIntroPhase.ShowingPrompt && elapsed - _phaseStarted >= PromptDuration)
            SetPhase(freshSpace ? GameIntroPhase.FadingOut : GameIntroPhase.AwaitingSpace, elapsed);
        else if (_phase == GameIntroPhase.AwaitingSpace && freshSpace)
            SetPhase(GameIntroPhase.FadingOut, elapsed);
        else if (_phase == GameIntroPhase.FadingOut && elapsed - _phaseStarted >= FadeDuration)
            SetPhase(GameIntroPhase.StoppingAudio, elapsed);

        return _phase switch
        {
            GameIntroPhase.Loading => new(_phase, 1, 0),
            GameIntroPhase.ShowingPrompt => new(_phase, 1, Progress(elapsed, PromptDuration)),
            GameIntroPhase.AwaitingSpace => new(_phase, 1, 1),
            GameIntroPhase.FadingOut => new(_phase, 1 - Progress(elapsed, FadeDuration), 1),
            _ => new(_phase, 0, 0)
        };
    }

    private void SetPhase(GameIntroPhase phase, TimeSpan at)
    {
        _phase = phase;
        _phaseStarted = at;
    }

    private double Progress(TimeSpan elapsed, TimeSpan duration) => (elapsed - _phaseStarted).TotalMilliseconds / duration.TotalMilliseconds;
}
