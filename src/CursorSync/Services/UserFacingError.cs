using Microsoft.Data.Sqlite;

namespace CursorSync.Services;

public static class UserFacingError
{
    public static string From(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (IsEmptyPath(current))
            {
                return "A required folder path was blank. Choose a sync folder in Settings if you have not yet. " +
                       "On the Agents page, chats without a known project folder are listed under Other until you open that folder in Cursor and refresh.";
            }

            if (IsReadOnlyDatabase(current))
            {
                return "Cursor still has the chat database open, or the file is read-only. Close Cursor completely (all windows), then try again.";
            }
        }

        return string.IsNullOrWhiteSpace(exception.Message)
            ? "Something went wrong."
            : exception.Message;
    }

    private static bool IsEmptyPath(Exception exception) =>
        exception is ArgumentException argument
        && (string.Equals(argument.ParamName, "path", StringComparison.OrdinalIgnoreCase)
            || argument.Message.Contains("path is empty", StringComparison.OrdinalIgnoreCase));

    private static bool IsReadOnlyDatabase(Exception exception) =>
        (exception is SqliteException sqlite && sqlite.SqliteErrorCode == 8)
        || exception.Message.Contains("readonly database", StringComparison.OrdinalIgnoreCase);
}
