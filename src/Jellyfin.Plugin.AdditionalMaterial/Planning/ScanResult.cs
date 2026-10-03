namespace Jellyfin.Plugin.AdditionalMaterial.Planning;

/// <summary>What VirusTotal knows about a file, looked up by its SHA-256 (the script's scan dict).</summary>
public sealed class ScanResult
{
    /// <summary>Gets or sets <c>clean</c>, <c>flagged</c> or <c>unknown</c>.</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Gets or sets how many engines call it malicious.</summary>
    public int Malicious { get; set; }

    /// <summary>Gets or sets how many engines call it suspicious.</summary>
    public int Suspicious { get; set; }

    /// <summary>Gets or sets how many engines gave an answer.</summary>
    public int Engines { get; set; }

    /// <summary>Gets or sets the file's page on VirusTotal.</summary>
    public string Link { get; set; } = string.Empty;

    /// <summary>The line written into a removal note: the script's describe_scan, word for word.</summary>
    /// <param name="scan">The result, or <c>null</c> when the file was not looked up.</param>
    /// <returns>The description.</returns>
    public static string Describe(ScanResult? scan) => scan?.Status switch
    {
        null => "not checked",
        "clean" => $"VirusTotal: known, flagged by 0 of {scan.Engines} engines",
        "flagged" => $"VirusTotal: FLAGGED by {scan.Malicious} engine(s) as malicious and {scan.Suspicious} as suspicious, out of {scan.Engines}",
        _ => "VirusTotal: not known to VirusTotal",
    };
}
