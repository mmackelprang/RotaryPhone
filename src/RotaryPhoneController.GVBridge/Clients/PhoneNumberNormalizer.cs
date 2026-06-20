namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Normalizes a user-typed phone number to E.164 for GV sendsms (ADR §4.2 #2). NANP/US scope only —
/// this is a single personal US GV account, and group/international threads are out of scope v1 (ADR §9).
/// Conservative by design: anything we cannot confidently map to a +1NXXNXXXXXX form is REJECTED
/// (returns false) rather than guessed, because a wrong number is an irreversible send to a stranger.
/// </summary>
public static class PhoneNumberNormalizer
{
    /// <summary>
    /// True + a +1NXXNXXXXXX string on success; false + null otherwise. Accepts +E.164, 10-digit NANP,
    /// or 11-digit (leading 1), with arbitrary punctuation/whitespace. Rejects empty, too-short,
    /// too-long, non-numeric, and non-+1 international forms.
    /// </summary>
    public static bool TryNormalize(string? input, out string? e164)
    {
        e164 = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        var hadPlus = trimmed.StartsWith('+');

        // Keep digits only.
        Span<char> buf = stackalloc char[trimmed.Length];
        var n = 0;
        foreach (var c in trimmed)
            if (char.IsDigit(c)) buf[n++] = c;
        var digits = new string(buf[..n]);

        if (digits.Length == 0) return false;

        // Explicit international (+ prefix and not +1...): out of scope v1 — reject, don't guess.
        if (hadPlus && !(digits.Length == 11 && digits[0] == '1'))
            return false;

        string tenDigits;
        if (digits.Length == 10)
            tenDigits = digits;                       // bare NANP
        else if (digits.Length == 11 && digits[0] == '1')
            tenDigits = digits[1..];                  // 1 + NANP
        else
            return false;                             // anything else is ambiguous → reject

        // NANP sanity: area code + exchange must start 2-9 (rough but catches obvious junk).
        if (tenDigits[0] is '0' or '1' || tenDigits[3] is '0' or '1')
            return false;

        e164 = "+1" + tenDigits;
        return true;
    }
}
