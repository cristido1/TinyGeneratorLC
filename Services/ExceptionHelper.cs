using System;
using System.Text;

namespace TinyGenerator.Services;

/// <summary>
/// Helper per la gestione delle eccezioni con supporto per inner exceptions.
/// </summary>
public static class ExceptionHelper
{
    /// <summary>
    /// Estrae il messaggio completo di un'eccezione includendo tutte le inner exceptions.
    /// </summary>
    /// <param name="ex">L'eccezione da cui estrarre il messaggio</param>
    /// <param name="includeStackTrace">Se includere anche lo stack trace (default: false)</param>
    /// <returns>Una stringa con tutti i messaggi delle eccezioni nella catena</returns>
    public static string GetFullExceptionMessage(Exception ex, bool includeStackTrace = false)
    {
        if (ex == null) return string.Empty;

        var sb = new StringBuilder();
        var currentEx = ex;
        var level = 0;

        while (currentEx != null)
        {
            if (level > 0)
            {
                sb.AppendLine();
                sb.Append($"[Inner Exception #{level}] ");
            }
            
            sb.Append($"{currentEx.GetType().Name}: {currentEx.Message}");
            
            if (includeStackTrace && !string.IsNullOrWhiteSpace(currentEx.StackTrace))
            {
                sb.AppendLine();
                sb.Append($"   Stack: {currentEx.StackTrace}");
            }

            currentEx = currentEx.InnerException;
            level++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Estrae solo i messaggi delle inner exceptions (senza il messaggio principale).
    /// </summary>
    /// <param name="ex">L'eccezione da cui estrarre i messaggi</param>
    /// <returns>Una stringa con i messaggi delle inner exceptions o string.Empty se non ce ne sono</returns>
    public static string GetInnerExceptionMessages(Exception ex)
    {
        if (ex?.InnerException == null) return string.Empty;

        var sb = new StringBuilder();
        var currentEx = ex.InnerException;
        var level = 1;

        while (currentEx != null)
        {
            if (level > 1) sb.Append(" -> ");
            sb.Append($"{currentEx.GetType().Name}: {currentEx.Message}");
            
            currentEx = currentEx.InnerException;
            level++;
        }

        return sb.ToString();
    }
}
