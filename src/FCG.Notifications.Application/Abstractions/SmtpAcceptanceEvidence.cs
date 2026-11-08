using System.Text.RegularExpressions;

namespace FCG.Notifications.Application.Abstractions;

// Nunca conserva texto livre, queue IDs, endereços ou resposta bruta.
public sealed class SmtpAcceptanceEvidence
{
    private SmtpAcceptanceEvidence(string value) => Value = value;
    public string Value { get; }
    public static SmtpAcceptanceEvidence FromSuccessfulResponse(string? response)
    {
        var match = Regex.Match(response ?? string.Empty, @"\A(?:250[ -])?(2\.[0-9]{1,3}\.[0-9]{1,3})(?:[ \r\n]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return new(match.Success ? "SMTP_ACCEPTED " + match.Groups[1].Value : "SMTP_ACCEPTED");
    }
}

public enum SentConfirmation { ConfirmedByAttempt, StillSending, StateChanged }
