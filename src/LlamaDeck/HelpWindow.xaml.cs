using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LlamaDeck;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        Add("Modeller",
            "Välj en mapp med GGUF-filer. LlamaDeck läser filernas metadata (arkitektur, antal lager, kontext) och visar storlek och kvantisering. " +
            "Delade modeller (00001-of-0000X) listas en gång.");

        Add("Server och port",
            "Starta/Stoppa startar llama-server som en barnprocess. Porten är den servern lyssnar på (standard 8080). " +
            "Med Nätverk på lyssnar servern på 0.0.0.0 och kan nås från andra enheter – sätt då gärna en API-nyckel under Avancerat.");

        Add("Backend: CPU, Vulkan, CUDA, ROCm, SYCL eller OpenCL",
            "CPU fungerar överallt. Vulkan fungerar på de flesta grafikkort (NVIDIA, AMD, Intel). CUDA är snabbast på NVIDIA. ROCm är AMD:s väg och " +
            "SYCL Intel:s. CUDA, ROCm och SYCL hämtas under Uppdateringar (stora nedladdningar). OpenCL stöds av llama.cpp för Intel- och Adreno-grafik " +
            "(inte NVIDIA/AMD); llama.cpp publicerar bara färdiga OpenCL-byggen för ARM64, så x64-varianten byggs från llama.cpp-källkoden och följer appens version.");

        Add("GPU-lager och VRAM-reserv",
            "GPU-lager avgör hur mycket av modellen som ligger i grafikkortets minne. Auto låter llama.cpp fylla minnet och lämnar din reserv fritt. " +
            "VRAM-reserv används som marginal per kort – höj den om skrivbordet eller andra program behöver minne.");

        Add("Flera grafikkort",
            "Alla kort som backend hittar listas. Kryssa i upp till fyra. Modellen fördelas automatiskt proportionellt mot ledigt minne " +
            "(llama.cpp:s tensor-split), och du kan finjustera med egna argument.");

        Add("När modellen är större än VRAM",
            "Auto (mmap): modellen ligger kvar på SSD och läses in vid behov. Det som får plats går till GPU, resten körs i RAM. " +
            "Det gör att stora modeller startar även med litet grafikkort, men blir långsammare. " +
            "Lås i RAM (mlock) hindrar Windows från att växla ut modellen. Läs in allt i RAM laddar hela filen i minnet.");

        Add("Reserv till Windows",
            "Windows behöver minne för att inte hänga sig. Med Hård RAM-gräns får serverprocessen aldrig ta mer än totalt RAM minus din reserv. " +
            "Gränsen sätts med ett jobbobjekt i Windows och gäller även om modellen är stor.");

        Add("Mätare och logg",
            "CPU, RAM och VRAM uppdateras varje sekund, och tok/s hämtas från serverns statistik. Loggen visar allt servern skriver. " +
            "Slå på Fil för att spara loggen, Auto för att följa med nedåt, och använd filtret för att hitta bland raderna.");

        Add("Uppdateringar",
            "Knappen Uppdateringar jämför dina installerade llama.cpp-byggen med senaste versionen på GitHub och kan ladda ner nya direkt. " +
            "Där finns också CUDA-runtime, Vulkan och OpenCL. För OpenCL visas systemets OpenCL-runtime och om llama.cpp har publicerat ett " +
            "x64-bygge – annars används den inbyggda varianten. Drivrutiner uppdateras bäst hos NVIDIA (länk finns i fönstret).");

        Add("API",
            "Servern är OpenAI-kompatibel: http://127.0.0.1:8080/v1/chat/completions. Öppna visar llama.cpp:s inbyggda webbgränssnitt. " +
            "Kopiera lägger adressen i urklipp. Testa skickar en liten fråga och visar svaret i loggen.");

        Add("Bra att veta",
            "Allt sparas i en JSON-fil i datamappen. Starta med --portable (eller lägg en portable.txt bredvid exe-filen) för att hålla allt i programmappen. " +
            "Stäng appen så avslutas även servern.");
    }

    private void Add(string title, string body)
    {
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 5),
            Foreground = (Brush)FindResource("TextBrush"),
        };
        var text = new TextBlock
        {
            Text = body,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Foreground = (Brush)FindResource("MutedBrush"),
        };
        ContentPanel.Children.Add(heading);
        ContentPanel.Children.Add(text);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
