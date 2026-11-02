namespace Riftway.BlazorSample;

/// <summary>One count for every window: a change in one re-renders the others.</summary>
public sealed class SharedCounter
{
    private int _value;

    public int Value => _value;

    public event Action? Changed;

    /// <summary>How many components listen: a closed window's component must have unsubscribed.</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    public void Increment()
    {
        Interlocked.Increment(ref _value);
        Changed?.Invoke();
    }
}
