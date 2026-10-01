using Vanara.PInvoke;

namespace Fischless.WindowsInput;

/// <summary>
/// Optional passive input-boundary callback. Observer failures never affect input dispatch.
/// The callback must not change the input array or send further input.
/// </summary>
public static class InputObservation
{
    public static Action<User32.INPUT[], bool>? Observer { get; set; }

    internal static void Notify(User32.INPUT[] inputs, bool completed)
    {
        try
        {
            Observer?.Invoke((User32.INPUT[])inputs.Clone(), completed);
        }
        catch
        {
            // Input delivery and its original exceptions remain owned by the dispatcher.
        }
    }
}
