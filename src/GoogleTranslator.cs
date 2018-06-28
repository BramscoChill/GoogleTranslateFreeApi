using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GoogleTranslateFreeApi.TranslationData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GoogleTranslateFreeApi
{
    public class GoogleTranslator : ITranslator
    {
        private readonly GoogleKeyTokenGenerator _generator;
        private static readonly Random _random = new Random();
        
        protected Uri _address;
        protected TimeSpan _timeOut;
        protected IWebProxy _proxy;

        private string currentGoogleTranslateCookie = null;

        public static int GetRandomNumber(int min, int max)
        {
            lock (_random) // synchronize
            {
                return _random.Next(min, max);
            }
        }

        /// <summary>
        /// Requests timeout
        /// </summary>
        public TimeSpan TimeOut
        {
            get { return _timeOut; }
            set
            {
                _timeOut = value;
                _generator.TimeOut = value;
            }
        }

        /// <summary>
        /// Requests proxy
        /// </summary>
        public IWebProxy Proxy
        {
            get { return _proxy; }
            set
            {
                _proxy = value;
                _generator.Proxy = value;
            }
        }

        public string Domain
        {
            get { return _address.AbsoluteUri.GetTextBetween("https://", "/translate_a/single"); }
            set { _address = new Uri($"https://{value}/translate_a/single"); }
        }

        /// <summary>
        /// An Array of supported languages by google translate
        /// </summary>
        public static Language[] LanguagesSupported { get; }

        /// <param name="language">Full name of the required language</param>
        /// <example>GoogleTranslator.GetLangaugeByName("English")</example>
        /// <returns>Language object from the LanguagesSupported array</returns>
        public static Language GetLanguageByName(string language)
            => LanguagesSupported.FirstOrDefault(i
                => i.FullName.Equals(language, StringComparison.OrdinalIgnoreCase));

        public static Language GetLanguageByISO(string iso)
            => LanguagesSupported.FirstOrDefault(i
                => i.ISO639.Equals(iso, StringComparison.OrdinalIgnoreCase));

        public static bool IsLanguageSupported(Language language)
        {
            if (language.Equals(Language.Auto))
                return true;

            return LanguagesSupported.Contains(language) ||
                   LanguagesSupported.FirstOrDefault(language.Equals) != null;
        }

        static GoogleTranslator()
        {
            var assembly = typeof(GoogleTranslator).GetTypeInfo().Assembly;
            Stream stream = assembly.GetManifestResourceStream("GoogleTranslateFreeApi.Languages.json");

            using (StreamReader reader = new StreamReader(stream))
            {
                string languages = reader.ReadToEnd();
                LanguagesSupported = JsonConvert
                    .DeserializeObject<Language[]>(languages);
            }
        }

        /// <param name="domain">A Domain name which will be used to execute requests</param>
        public GoogleTranslator(string domain = "translate.google.com")
        {
            _address = new Uri($"https://{domain}/translate_a/single");
            _generator = new GoogleKeyTokenGenerator();
        }

        /// <summary>
        /// <p>
        /// Async text translation from language to language. Include full information about the translation.
        /// </p>
        /// </summary>
        /// <param name="originalText">Text to translate</param>
        /// <param name="fromLanguage">Source language</param>
        /// <param name="toLanguage">Target language</param>
        /// <exception cref="LanguageNotSupportException">Language is not supported</exception>
        /// <exception cref="InvalidOperationException">Thrown when target language is auto</exception>
        /// <exception cref="GoogleTranslateIPBannedException">Thrown when the IP used for requests is banned </exception>
        /// <exception cref="WebException">Thrown when getting an error with response</exception>
        public async Task<TranslationResult> TranslateAsync(string originalText, Language fromLanguage,
            Language toLanguage)
        {
            return await GetTranslationResultAsync(originalText, fromLanguage, toLanguage, true);
        }

        /// <summary>
        /// <p>
        /// Async text translation from language to language. Include full information about the translation.
        /// </p>
        /// </summary>
        /// <param name="item">The object that implements the interface ITranslatable</param>
        /// <exception cref="LanguageNotSupportException">Language is not supported</exception>
        /// <exception cref="InvalidOperationException">Thrown when target language is auto</exception>
        /// <exception cref="GoogleTranslateIPBannedException">Thrown when the IP used for requests is banned </exception>
        /// <exception cref="WebException">Thrown when getting an error with response</exception>
        public async Task<TranslationResult> TranslateAsync(ITranslatable item)
        {
            return await TranslateAsync(item.OriginalText, item.FromLanguage, item.ToLanguage);
        }

        /// <summary>
        /// <p>
        /// Async text translation from language to language. 
        /// In contrast to the TranslateAsync doesn't include additional information such as ExtraTranslation and Definition.
        /// </p>
        /// </summary>
        /// <param name="originalText">Text to translate</param>
        /// <param name="fromLanguage">Source language</param>
        /// <param name="toLanguage">Target language</param>
        /// <exception cref="LanguageNotSupportException">Language is not supported</exception>
        /// <exception cref="InvalidOperationException">Thrown when target language is auto</exception>
        /// <exception cref="GoogleTranslateIPBannedException">Thrown when the IP used for requests is banned </exception>
        /// <exception cref="WebException">Thrown when getting an error with response</exception>
        public async Task<TranslationResult> TranslateLiteAsync(string originalText, Language fromLanguage,
            Language toLanguage)
        {
            return await GetTranslationResultAsync(originalText, fromLanguage, toLanguage, false);
        }

        /// <summary>
        /// <p>
        /// Async text translation from language to language. 
        /// In contrast to the TranslateAsync doesn't include additional information such as ExtraTranslation and Definition.
        /// </p>
        /// </summary>
        /// <param name="item">The object that implements the interface ITranslatable</param>
        /// <exception cref="LanguageNotSupportException">Language is not supported</exception>
        /// <exception cref="InvalidOperationException">Thrown when target language is auto</exception>
        /// <exception cref="GoogleTranslateIPBannedException">Thrown when the IP used for requests is banned </exception>
        /// <exception cref="WebException">Thrown when getting an error with response</exception>
        public async Task<TranslationResult> TranslateLiteAsync(ITranslatable item)
        {
            return await TranslateLiteAsync(item.OriginalText, item.FromLanguage, item.ToLanguage);
        }

        protected async virtual Task<TranslationResult> GetTranslationResultAsync(string originalText,
            Language fromLanguage,
            Language toLanguage, bool additionInfo)
        {
            if (!IsLanguageSupported(fromLanguage))
                throw new LanguageNotSupportException(fromLanguage);
            if (!IsLanguageSupported(toLanguage))
                throw new LanguageNotSupportException(toLanguage);
            if (toLanguage.Equals(Language.Auto))
                throw new InvalidOperationException("A destination Language is auto");

            if (originalText.Trim() == String.Empty)
                return new TranslationResult();

            //sets the cookie first to prevent being blocked out
            SetGoogleTranslateCookie();

            string token = await _generator.GenerateAsync(originalText);
            
            string urlQueryString = $"?sl={fromLanguage.ISO639}&" +
                                    $"tl={toLanguage.ISO639}&" +
                                    $"hl=en&" +
                                    $"q={Uri.EscapeDataString(originalText)}&" +
                                    $"tk={token}&" +
                                    "client=t&" +
                                    "dt=at&dt=bd&dt=ex&dt=ld&dt=md&dt=qca&dt=rw&dt=rm&dt=ss&dt=t&" +
                                    "ie=UTF-8&" +
                                    "oe=UTF-8&" +
                                    "otf=1&" +
                                    "ssel=0&" +
                                    "tsel=0&" +
                                    "kc=7";

            //build in a delay of random, prevention for getting banned / blocked
            //not sure if this will work, bud let's try
            await Task.Delay(GetRandomNumber(200, 500));

            var baseUri = new Uri(_address, urlQueryString);
            HttpWebRequest request = WebRequest.CreateHttp(baseUri);
            request.Proxy = Proxy;
            request.ContentType = "text/plain";
            request.Method = HttpMethod.Get.Method;
            request.KeepAlive = true;
            //request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.87 Safari/537.36";
            //request.UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/46.0.2490.71 Safari/537.36";
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:61.0) Gecko/20100101 Firefox/61.0";
            request.Host = "translate.google.com";
            request.Headers["Accept-Language"] = "en-US,en;q=0.5";
            
            //sets the cookie to the request to prevent banning
            CookieContainer container = new CookieContainer();
            string currentCookie = currentGoogleTranslateCookie ?? "NID=132=YSV6D_1_0-kurlU0FU1_McKljflccBTuJEM4tGzFWw8nZm90f-P7bzqrFnETlu4LLDf5GMwAD2oiRicTUeP_fftLO7Xy2OH0Vz2MerRlalbfmfHOf1Lrn3EN-_C3Pk2Y; CONSENT=WP.26e489; 1P_JAR=2018-6-18-12; _ga=GA1.3.737450149.1529324066; _gid=GA1.3.606173287.1529324066";
            container.Add(baseUri, GeneralHelper.GetAllCookiesFromHeader(currentCookie, baseUri.Host));
            request.CookieContainer = container;

            request.ContinueTimeout = (int) TimeOut.TotalMilliseconds;

            HttpWebResponse response = null;

            try
            {
                response = (HttpWebResponse) await request.GetResponseAsync();
            }
            catch (WebException e)
            {
                if (_generator.IsExternalKeyObsolete)
                    await TranslateAsync(originalText, fromLanguage, toLanguage);
                else if ((int) e.Status == 7) //ProtocolError
                    throw new GoogleTranslateIPBannedException(GoogleTranslateIPBannedException.Operation.Translation);
                else
                    throw;
            }

            string result;
            using (StreamReader sr = new StreamReader(response.GetResponseStream()))
                result = sr.ReadToEnd();

            //if a new cookie is provided, remember it
            if (response.Headers.AllKeys.Any(key => key.Equals("Set-Cookie")))
            {
                currentGoogleTranslateCookie = response.Headers["Set-Cookie"];
            }

            return ResponseToTranslateResultParse(result, originalText, fromLanguage, toLanguage, additionInfo);
        }
        private void SetGoogleTranslateCookie()
        {
            if (currentGoogleTranslateCookie == null)
            {
                var baseUri = new Uri("http://translate.google.nl/");
                HttpWebRequest request = WebRequest.CreateHttp(baseUri);
                request.Proxy = Proxy;
                request.Method = HttpMethod.Get.Method;
                request.KeepAlive = true;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:61.0) Gecko/20100101 Firefox/61.0";
                request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

                request.Host = "translate.google.com";
                request.Headers["Accept-Encoding"] = "gzip,deflate";
                request.Headers["Accept-Charset"] = "ISO-8859-1,utf-8;q=0.7,*;q=0.7";
                request.Headers["Accept-Language"] = "en-US,en;q=0.5";

                request.ContinueTimeout = (int)TimeOut.TotalMilliseconds;

                HttpWebResponse response = null;

                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                    if(response.Headers.AllKeys.Any(key => key.Equals("Set-Cookie")))
                    {
                        currentGoogleTranslateCookie = response.Headers["Set-Cookie"];
                    }
                }
                catch (WebException e)
                {

                }
            }
        }
        protected virtual TranslationResult ResponseToTranslateResultParse(string result, string sourceText,
            Language sourceLanguage, Language targetLanguage, bool additionInfo)
        {
            TranslationResult translationResult = new TranslationResult();

            JToken tmp = JsonConvert.DeserializeObject<JToken>(result);

            string originalTextTranscription = null, translatedTextTranscription = null;
            string[] translate;

            var mainTranslationInfo = tmp[0];

            GetMainTranslationInfo(mainTranslationInfo, out translate,
                ref originalTextTranscription, ref translatedTextTranscription);

            translationResult.FragmentedTranslation = translate;
            translationResult.OriginalText = sourceText;

            translationResult.OriginalTextTranscription = originalTextTranscription;
            translationResult.TranslatedTextTranscription = translatedTextTranscription;

            translationResult.Corrections = GetTranslationCorrections(tmp);

            translationResult.SourceLanguage = sourceLanguage.Equals(Language.Auto)
                ? GetLanguageByISO((string) tmp[8][0][0])
                : sourceLanguage;

            translationResult.TargetLanguage = targetLanguage;

            if (!additionInfo)
                return translationResult;

            translationResult.ExtraTranslations =
                TranslationInfoParse<ExtraTranslations>(tmp[1]);

            translationResult.Synonyms = tmp.Count() >= 12
                ? TranslationInfoParse<Synonyms>(tmp[11])
                : null;

            translationResult.Definitions = tmp.Count() >= 13
                ? TranslationInfoParse<Definitions>(tmp[12])
                : null;

            translationResult.SeeAlso = tmp.Count() >= 15
                ? GetSeeAlso(tmp[14])
                : null;

            return translationResult;
        }

        protected static T TranslationInfoParse<T>(JToken response) where T : TranslationInfoParser
        {
            if (!response.HasValues)
                return null;

            T translationInfoObject = TranslationInfoParser.Create<T>();

            foreach (var item in response)
            {
                string partOfSpeech = (string) item[0];

                JToken itemToken = translationInfoObject.ItemDataIndex == -1
                    ? item
                    : item[translationInfoObject.ItemDataIndex];

                //////////////////////////////////////////////////////////////
                // I delete the white spaces to work auxiliary verb as well //
                //////////////////////////////////////////////////////////////
                if (!translationInfoObject.TryParseMemberAndAdd(partOfSpeech.Replace(' ', '\0'), itemToken))
                {
#if DEBUG
                    //sometimes response contains members without name. Just ignore it.
                    Debug.WriteLineIf(partOfSpeech.Trim() != String.Empty,
                        $"class {typeof(T).Name} dont contains a member for a part " +
                        $"of speech {partOfSpeech}");
#endif
                }
            }

            return translationInfoObject;
        }

        protected static string[] GetSeeAlso(JToken response)
        {
            return !response.HasValues ? new string[0] : response[0].ToObject<string[]>();
        }

        protected static void GetMainTranslationInfo(JToken translationInfo, out string[] translate,
            ref string originalTextTranscription, ref string translatedTextTranscription)
        {
            bool transcriptionAviable = translationInfo.Count() > 1;

            translate = new string[translationInfo.Count() - (transcriptionAviable ? 1 : 0)];

            for (int i = 0; i < translate.Length; i++)
                translate[i] = (string) translationInfo[i][0];


            if (!transcriptionAviable)
                return;

            var transcriptionInfo = translationInfo[translationInfo.Count() - 1];
            int elementsCount = transcriptionInfo.Count();

            if (elementsCount == 3)
            {
                translatedTextTranscription = (string) transcriptionInfo[elementsCount - 1];
            }
            else
            {
                if (transcriptionInfo[elementsCount - 2] != null)
                    translatedTextTranscription = (string) transcriptionInfo[elementsCount - 2];
                else
                    translatedTextTranscription = (string) transcriptionInfo[elementsCount - 1];

                originalTextTranscription = (string) transcriptionInfo[elementsCount - 1];
            }
        }

        protected static Corrections GetTranslationCorrections(JToken response)
        {
            if (!response.HasValues)
                return new Corrections();

            Corrections corrections = new Corrections();

            JToken textCorrectionInfo = response[7];

            if (textCorrectionInfo.HasValues)
            {
                Regex pattern = new Regex(@"<b><i>(.*?)</i></b>");
                MatchCollection matches = pattern.Matches((string) textCorrectionInfo[0]);

                var correctedText = (string) textCorrectionInfo[1];
                var correctedWords = new string[matches.Count];

                for (int i = 0; i < matches.Count; i++)
                    correctedWords[i] = matches[i].Groups[1].Value;

                corrections.CorrectedWords = correctedWords;
                corrections.CorrectedText = correctedText;
                corrections.TextWasCorrected = true;
            }

            string selectedLangauge = (string) response[2];
            string detectedLanguage = (string) (response[8])[0][0];

            if (selectedLangauge != detectedLanguage)
            {
                corrections.LanguageWasCorrected = true;
                corrections.CorrectedLanguage = LanguagesSupported.FirstOrDefault(language =>
                    language.ISO639 == detectedLanguage);
            }

            corrections.Confidence = (double) response[6];

            return corrections;
        }
    }
}