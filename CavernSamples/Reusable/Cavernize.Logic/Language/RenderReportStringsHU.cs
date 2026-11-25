namespace Cavernize.Logic.Language;

/// <summary>
/// Strings used for generating a post-render report in Hungarian.
/// </summary>
public class RenderReportStringsHU : RenderReportStrings {
    /// <inheritdoc/>
    protected override string CultureCode => "hu-HU";

    /// <inheritdoc/>
    protected override void ApplyTranslation() {
        Set("Defau", "Miután a renderelés befejeződött, itt további sávinformációt találsz, mint például a valódi objektumhasználati statisztika.");
        Set("ABeds", "Valójában tartalmazott bed csatornák");
        Set("AObjs", "Valójában tartalmazott dinamikus objektumok");
        Set("FakeT", "Kihasználatlan (kamu) renderelési célok");
        Set("PeaGa", "Csúcs audio keret szint");
        Set("RMSGa", "Tartalom RMS szintje");
        Set("MacDy", "Makrodinamika");
        Set("MicDy", "Mikrodinamika");
        Set("NoLFE", "Az LFE csatorna hiányzott a forrásból, kihasználatlan volt, vagy nem lett renderelve.");
        Set("PeaLF", "Csúcs LFE szint");
        Set("RMSLF", "RMS LFE szint");
        Set("MacLF", "LFE makrodinamika");
        Set("MicLF", "LFE mikrodinamika");
        Set("CheSl", "Mellbevágási osztályzat");
        Set("SurUs", "Surround használat");
        Set("HeiUs", "Magasság használat");
        Set("PcmPre", "Renderelt PCM-mintacsúcsok a limiter előtt");
        Set("PcmOut", "Feldolgozott PCM-mintacsúcsok exportálás előtt");
        Set("PcmOk", "Teljes műsor");
        Set("PcmInc", "Megfigyelt csúcsok — hiányos elemzés");
        Set("PcmUnv", "Megfigyelt csúcsok — a forrás teljessége nem igazolt");
        Set("PcmFrames", "megfigyelt / várt PCM-mintakeret");
        Set("PcmAll", "Legnagyobb mintacsúcs");
        Set("PcmOverallRms", "Teljes műsor RMS-szintje");
        Set("PcmWindow", "Az aktivitást {0} ms-os RMS-ablakok alapján, időtartammal súlyozva mérjük.");
        Set("PcmSpeaker", "Csatorna");
        Set("PcmPeakColumn", "Csúcs");
        Set("PcmRmsColumn", "Teljes műsor RMS");
        Set("PcmActive", "Aktív >{0} dBFS");
        Set("PcmVerification", "A csúcsok ellenőrzése");
        Set("PcmMatched", "Egyezik");
        Set("PcmMismatch", "Nem egyezik");
        Set("PcmNA", "Nem érhető el");
        Set("PcmMissingBlock", "Egy vagy több várt PCM-mintakeret hiányzik vagy érvénytelen.");
        Set("PcmInvalidBuffer", "Egy blokk mérete vagy a mérési hívások száma eltért a várttól.");
        Set("PcmNonFinitePcm", "Egy blokk NaN vagy végtelen PCM-értéket tartalmazott.");
        Set("PcmRenderError", "Egy blokk renderelése sikertelen volt. A behelyettesített csend kimaradt a mérésből.");
        Set("PcmLayoutChanged", "Megváltozott a hallgatói csatornakiosztás vagy a blokk beállítása.");
        Set("PcmSourceLengthMismatch", "A forrás hossza vagy mintavételi frekvenciája eltért a renderelésétől.");
        Set("PcmInvalidSource", "A forrás PCM-adatai csonkák vagy nem egész mintakeretekből állnak.");
        Set("PcmInterrupted", "A renderelés vagy az elemzés hibával ért véget.");
        Set("PcmMeasurementError", "A limiter előtti mérés hibába ütközött.");
        Set("Grad0", "5*");
        Set("Grad1", "5");
        Set("Grad2", "4");
        Set("Grad3", "3");
        Set("Grad4", "2");
        Set("Grad5", "1");
    }
}
