namespace ComicMaintainer.Core.Utilities;

public static class SuwayomiImportSynchronization
{
    private static readonly object Gate = new();

    public static T Execute<T>(Func<T> action)
    {
        lock (Gate)
        {
            return action();
        }
    }

    public static void Execute(Action action)
    {
        lock (Gate)
        {
            action();
        }
    }
}
