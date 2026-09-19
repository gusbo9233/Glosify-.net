namespace Glosify.Models;

public sealed record HomeLanguageQuote(string Original, string English, string Attribution);

public static class HomeLanguageQuoteCatalog
{
    // Literary excerpts, translation notes, and sources: docs/home-language-quotes.md.
    private static readonly IReadOnlyDictionary<string, HomeLanguageQuote> Quotes =
        new Dictionary<string, HomeLanguageQuote>(StringComparer.OrdinalIgnoreCase)
        {
            ["af"] = Q("Afrikaans is die taal wat vir Wes-Europa en Afrika verbind.", "Afrikaans is the language that connects Western Europe and Africa.", "N. P. van Wyk Louw"),
            ["ar"] = Q("أنا البحر في أحشائه الدر كامن، فهل سألوا الغواص عن صدفاتي؟", "I am the sea, with pearls hidden in my depths; have they asked the diver about my shells?", "Hafiz Ibrahim — The Arabic Language"),
            ["hy"] = Q("Մեր լեզուն մեր խիղճն է դա…", "Our language is our conscience…", "Hamo Sahyan — Our Language"),
            ["as"] = Q("শিকাৰ কোনো শেষ নাই।", "There is no end to learning.", "Assamese proverb"),
            ["az"] = Q("Bu dil bizim ruhumuz, eşqimiz, canımızdır.", "This language is our spirit, our love, our soul.", "Bəxtiyar Vahabzadə — Ana dilim"),
            ["bn"] = Q("হে বঙ্গ, ভাণ্ডারে তব বিবিধ রতন…", "O Bengal, your treasury holds jewels of many kinds…", "Michael Madhusudan Dutt — Bangabhasha"),
            ["bs"] = Q("Koliko jezika znaš, toliko ljudi vrijediš.", "You are worth as many people as the languages you know.", "Bosnian proverb"),
            ["bg"] = Q("Език свещен на моите деди…", "Sacred language of my ancestors…", "Ivan Vazov — The Bulgarian Language"),
            ["my"] = Q("ပညာရွှေအိုး လူမခိုး။", "Knowledge is a pot of gold no thief can steal.", "Burmese proverb"),
            ["yue"] = Q("學海無涯，唯勤是岸。", "The sea of learning has no shore; diligence is the way across.", "Cantonese proverb"),
            ["ca"] = Q("En llemosí sonà lo meu primer vagit…", "In my native Catalan rang out my very first cry…", "Bonaventura Carles Aribau — La pàtria"),
            ["zh-Hans"] = Q("言之无文，行而不远。", "Words without beauty do not travel far.", "Zuo Zhuan — Duke Xiang, year 25"),
            ["hr"] = Q("O jeziku, rode, da ti pojem, o jeziku milom tvom i mojem!", "My people, let me sing to you of language, of the dear language that is yours and mine!", "Petar Preradović — Rodu o jeziku"),
            ["cs"] = Q("Kolik řečí umíš, tolikrát jsi člověkem.", "You live as many lives as the languages you know.", "Czech proverb"),
            ["da"] = Q("Du danske Sprog, Du er min Moders Stemme…", "You Danish language, you are my mother’s voice…", "H. C. Andersen — Danmark, mit Fædreland"),
            ["nl"] = Q("Als de ziele luistert, spreekt het al een taal dat leeft…", "When the soul listens, everything that lives speaks a language…", "Guido Gezelle — Als de ziele luistert"),
            ["en"] = Q("Language is the dress of thought.", "Language is the dress of thought.", "Samuel Johnson — Life of Cowley"),
            ["et"] = Q("Kas siis selle maa keel laulu tules ei või taevani tõustes üles igavikku omale otsida?", "Cannot the language of this land, rising to the heavens in the fire of song, seek eternity for itself?", "Kristjan Jaak Peterson — Kuu"),
            ["fil"] = Q("Ang hindi magmahal sa kanyang salita, mahigit sa hayop at malansang isda.", "One who does not love their own language is worse than a beast and a foul-smelling fish.", "Sa Aking Mga Kabata — authorship disputed"),
            ["fi"] = Q("Mieleni minun tekevi, aivoni ajattelevi lähteäni laulamahan, saa’ani sanelemahan…", "My mind yearns, my thoughts stir, to begin singing, to give voice to words…", "Kalevala — compiled by Elias Lönnrot"),
            ["fr"] = Q("Ma patrie, c’est la langue française.", "My homeland is the French language.", "Albert Camus"),
            ["gl"] = Q("O idioma é a chave coa que abrimos o mundo.", "Language is the key with which we open the world.", "Manuel María — A fala"),
            ["ka"] = Q("სამი ღვთაებრივი საუნჯე დაგვრჩა ჩვენ მამა-პაპათაგან: მამული, ენა და სარწმუნოება.", "Three divine treasures were left to us by our ancestors: homeland, language, and faith.", "Ilia Chavchavadze"),
            ["de"] = Q("Wer fremde Sprachen nicht kennt, weiß nichts von seiner eigenen.", "Those who know no foreign languages know nothing of their own.", "Johann Wolfgang von Goethe"),
            ["el"] = Q("Τη γλώσσα μού έδωσαν ελληνική…", "The language they gave me was Greek…", "Odysseas Elytis — To Axion Esti"),
            ["gu"] = Q("જ્ઞાન એ શક્તિ છે.", "Knowledge is power.", "Gujarati saying"),
            ["ha"] = Q("Harshe ɗaya ba ya cika baki.", "One language does not fill the mouth.", "Hausa saying"),
            ["he"] = Q("מות וחיים ביד לשון.", "Death and life are in the power of the tongue.", "Proverbs 18:21"),
            ["hi"] = Q("निज भाषा उन्नति अहै, सब उन्नति को मूल।", "The advancement of one’s own language is the root of all progress.", "Bharatendu Harishchandra"),
            ["hu"] = Q("Ahány nyelvet beszélsz, annyi ember vagy.", "You are as many people as the languages you speak.", "Hungarian proverb"),
            ["is"] = Q("Ástkæra, ylhýra málið, og allri rödd fegra…", "Beloved, warm-hearted language, more beautiful than any voice…", "Jónas Hallgrímsson — Ásta"),
            ["id"] = Q("Bahasa menunjukkan bangsa.", "Language reveals a people.", "Indonesian proverb"),
            ["it"] = Q("Chi parla due lingue vale per due.", "Who speaks two languages is worth two people.", "Italian proverb"),
            ["ja"] = Q("言葉は心の使い。", "Words are the messengers of the heart.", "Japanese proverb"),
            ["kn"] = Q("ಎಲ್ಲಾದರು ಇರು ಎಂತಾದರು ಇರು ಎಂದೆಂದಿಗು ನೀ ಕನ್ನಡವಾಗಿರು…", "Wherever you are, whatever you become, remain forever true to Kannada…", "Kuvempu"),
            ["kk"] = Q("Өзге тілдің бәрін біл, өз тіліңді құрметте.", "Learn every other language; cherish your own.", "Qadyr Myrza Ali"),
            ["ko"] = Q("말이 오르면 나라도 오르고 말이 내리면 나라도 내리나니라.", "When a language rises, its nation rises; when a language falls, its nation falls.", "Ju Si-gyeong — Hannaramal"),
            ["ky"] = Q("Өнөр алды — кызыл тил.", "The greatest art is eloquent speech.", "Kyrgyz proverb"),
            ["lv"] = Q("Cik valodu tu zini, tik cilvēku tu esi.", "You are as many people as the languages you know.", "Latvian proverb"),
            ["lt"] = Q("Kiek kalbų moki, tiek kartų esi žmogus.", "You are a person as many times as the languages you know.", "Lithuanian proverb"),
            ["mk"] = Q("Јазикот е заправо единствената наша комплетна татковина.", "Language is, in fact, our only complete homeland.", "Blaže Koneski"),
            ["ms"] = Q("Bahasa jiwa bangsa.", "Language is the soul of a nation.", "Malay proverb"),
            ["ml"] = Q("വിദ്യാധനം സർവ്വധനാൽ പ്രധാനം.", "The wealth of knowledge is greater than every other wealth.", "Malayalam proverb"),
            ["mt"] = Q("Kemm taf ilsna, kemm int nies.", "You are as many people as the languages you know.", "Maltese proverb"),
            ["mi"] = Q("Ko te reo te mauri o te mana Māori.", "Language is the life force of Māori identity.", "Sir James Henare"),
            ["mr"] = Q("माझा मराठाचि बोलु कौतुकें । परि अमृतातेंही पैजां जिंके ।", "Such is the wonder of my Marathi speech that it outdoes even nectar in sweetness.", "Dnyaneshwar — Dnyaneshwari"),
            ["ne"] = Q("ज्ञान नै शक्ति हो।", "Knowledge is power.", "Nepali saying"),
            ["nb"] = Q("Nytt språk, nytt liv.", "A new language is a new life.", "Norwegian saying"),
            ["or"] = Q("ଜ୍ଞାନ ହିଁ ଶକ୍ତି।", "Knowledge is power.", "Odia saying"),
            ["fa"] = Q("زبان سرخ سر سبز می‌دهد بر باد.", "A sharp tongue can bring a flourishing life to ruin.", "Persian proverb"),
            ["pl"] = Q("A niechaj narodowie wżdy postronni znają, iż Polacy nie gęsi, iż swój język mają.", "Let foreign nations know that Poles have a language of their own.", "Mikołaj Rej"),
            ["pt"] = Q("Minha pátria é a língua portuguesa.", "My homeland is the Portuguese language.", "Fernando Pessoa — Bernardo Soares"),
            ["pa"] = Q("ਅਸੀਂ ਨਹੀਂ ਭੁਲਾਉਣੀ, ਬੋਲੀ ਹੈ ਪੰਜਾਬੀ ਸਾਡੀ।", "We will not forget it: Punjabi is our language.", "Dhani Ram Chatrik"),
            ["ro"] = Q("Limba noastră-i o comoară…", "Our language is a treasure…", "Alexei Mateevici — Limba noastră"),
            ["ru"] = Q("…о великий, могучий, правдивый и свободный русский язык!", "…O great, mighty, truthful, and free Russian language!", "Ivan Turgenev — The Russian Language"),
            ["sr-Latn"] = Q("Koliko jezika znaš, toliko ljudi vrediš.", "You are worth as many people as the languages you know.", "Serbian proverb"),
            ["sk"] = Q("Ó, mojej matky reč je krásota…", "Oh, my mother’s language is beauty itself…", "Pavol Országh Hviezdoslav — Letorosty III"),
            ["sl"] = Q("Kolikor jezikov znaš, toliko veljaš.", "You are worth as much as the languages you know.", "Slovenian proverb"),
            ["es"] = Q("La sangre de mi espíritu es mi lengua, y mi patria es allí donde resuene…", "My language is the blood of my spirit, and my homeland is wherever it resounds…", "Miguel de Unamuno — La sangre de mi espíritu"),
            ["sw"] = Q("Lugha ni daraja la maarifa.", "Language is a bridge to knowledge.", "Swahili saying"),
            ["sv"] = Q("Ärans och hjältarnas språk! Hur ädelt och manligt du rör dig!", "Language of honour and heroes! How nobly and manfully you move!", "Esaias Tegnér — Språken"),
            ["ta"] = Q("யாமறிந்த மொழிகளிலே தமிழ்மொழிபோல் இனிதாவது எங்கும் காணோம்…", "Of all the languages we know, nowhere have we found one as sweet as Tamil…", "Subramania Bharati — Tamil"),
            ["te"] = Q("దేశ భాషలందు తెలుగు లెస్స.", "Among the languages of the land, Telugu is the finest.", "Sri Krishnadevaraya"),
            ["th"] = Q("รู้ภาษา รู้โลก.", "Know languages, know the world.", "Thai saying"),
            ["tr"] = Q("Söz ola kese savaşı…", "A word can end a war…", "Yunus Emre"),
            ["uk"] = Q("Буду я навчатись мови золотої…", "I will learn the golden language…", "Andrii Malyshko"),
            ["uz"] = Q("Til bilgan — el biladi.", "Who knows a language knows its people.", "Uzbek proverb"),
            ["vi"] = Q("Truyện Kiều còn, tiếng Việt còn, tiếng Việt còn, nước Nam còn.", "As long as The Tale of Kiều endures, Vietnamese endures; as long as Vietnamese endures, Vietnam endures.", "Phạm Quỳnh"),
            ["cy"] = Q("Cenedl heb iaith, cenedl heb galon.", "A nation without a language is a nation without a heart.", "Welsh proverb"),
        };

    public static HomeLanguageQuote? Find(string? languageCode) =>
        languageCode is not null && Quotes.TryGetValue(languageCode, out var quote) ? quote : null;

    public static IReadOnlyCollection<string> LanguageCodes { get; } = Quotes.Keys.ToArray();

    private static HomeLanguageQuote Q(string original, string english, string attribution) =>
        new(original, english, attribution);
}
