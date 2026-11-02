using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>A Views button's hover animation, as Electron's caption buttons use it: <c>gfx::SlideAnimation</c> with
/// <c>views::Button</c>'s 150 ms, ease-out, driven by <c>Button::SetState</c>. Time is passed in, in milliseconds.</summary>
internal sealed class HoverAnimation
{
    /// <summary><c>views::Button</c>'s hover slide duration.</summary>
    internal const double SlideMs = 150;

    /// <summary><c>gfx::LinearAnimation</c>'s default frame interval, 60 Hz.</summary>
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromMicroseconds(1000000 / 60);

    private bool? _showing;
    private double _start;
    private double _end;
    private double _startMs;
    private double _durationMs;

    /// <summary>The hover amount, 0 to 1.</summary>
    internal double Value { get; private set; }

    /// <summary>Whether the value is still moving.</summary>
    internal bool IsAnimating { get; private set; }

    /// <summary>Electron's <c>Button::SetState</c> with <c>animate_on_state_change</c>: hovered to normal fades out,
    /// normal to hovered fades in, any other change to hovered jumps to 1, any change to another state jumps to 0.</summary>
    internal void OnStateChanged(cef_button_state_t from, cef_button_state_t to, double nowMs)
    {
        if (from == to) return;
        if (from == cef_button_state_t.CEF_BUTTON_STATE_HOVERED && to == cef_button_state_t.CEF_BUTTON_STATE_NORMAL) Begin(showing: false, nowMs);
        else if (to != cef_button_state_t.CEF_BUTTON_STATE_HOVERED) Reset(0);
        else if (from == cef_button_state_t.CEF_BUTTON_STATE_NORMAL) Begin(showing: true, nowMs);
        else Reset(1);
    }

    /// <summary>Moves the value to where it is at <paramref name="nowMs"/>; <c>LinearAnimation</c> then <c>AnimateToState</c>.</summary>
    internal void Step(double nowMs)
    {
        if (!IsAnimating) return;
        var t = Math.Clamp((nowMs - _startMs) / _durationMs, 0, 1);
        var state = 1.0 - Math.Pow(1.0 - t, 2);
        if (state == 1.0) _showing = null;
        Value = _start + (_end - _start) * state;
        if (_end >= _start ? Value > _end : Value < _end) Value = _end;
        if (t >= 1) IsAnimating = false;
    }

    private void Reset(double value)
    {
        _showing = null;
        Value = value;
        IsAnimating = false;
    }

    // SlideAnimation::BeginAnimating: a partly done slide takes the remaining share of the duration.
    private void Begin(bool showing, double nowMs)
    {
        if (_showing == showing) return;
        _showing = showing;
        _start = Value;
        _end = showing ? 1.0 : 0.0;
        if (Value == _end) return;
        _durationMs = SlideMs * (1 - (showing ? Value : 1.0 - Value));
        _startMs = nowMs;
        IsAnimating = true;
    }
}
