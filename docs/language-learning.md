# Language learning in the web player

Enable **Language learning** in playback settings or the video player's settings menu, then select an English text subtitle track. Clicking a word pauses playback and opens a dialog with its Ukrainian translation, English contextual explanation, lemma, and part of speech. **Translate sentence** adds the complete sentence and its Ukrainian translation. **Show previous sentences** adds up to three preceding sentences and translations. Closing the dialog resumes playback only if opening it paused the video.

The star saves the selected occurrence, its complete original sentence, and the Ukrainian sentence translation to the current user's vocabulary. The server obtains the sentence translation on save even if the player dialog has not been expanded. Clicking the star again removes the entry. Different occurrences of the same word can be saved separately. Original text and translations remain saved even if the subtitle file or media item is removed. Deleting the user deletes their vocabulary.

Open **My vocabulary** from the web navigation menu or your own user settings. The page shows the saved word, Ukrainian word translation, highlighted English subtitle sentence, Ukrainian sentence translation, part of speech, and an expandable English explanation with the base form. Search matches saved words, translations, sentences, and explanations across all pages. Entries can be removed with confirmation. Older entries missing a sentence translation offer a button to translate and save the original sentence directly from its stored text.

## Server configuration

Inference runs on the Jellyfin backend through OpenRouter. Configure the API key using the server's environment configuration provider:

```sh
JELLYFIN_LanguageLearning__ApiKey=your-openrouter-key
JELLYFIN_LanguageLearning__Model=openai/gpt-6-luna
```

The default model is `openai/gpt-6-luna`. The key is never sent to the web client. If using Docker Compose, pass these environment variables through the Jellyfin service's `environment` configuration, then recreate that service. Do not commit the API key. The existing deployment configuration is not modified by this feature.

The model ID is documented at <https://openrouter.ai/openai/gpt-6-luna>. OpenRouter currently publishes official TypeScript, Python, and Go client SDKs, but no .NET SDK: <https://openrouter.ai/docs/client-sdks/overview>. The .NET server uses a typed wrapper around `IHttpClientFactory` and OpenRouter's chat completions API, with structured JSON responses.

English text subtitle excerpts are sent to OpenRouter only after a word or expansion is clicked. The server reads subtitles itself, checks media access, verifies the exact word occurrence, and reconstructs the sentence across cue boundaries. Sentence detection uses punctuation, common abbreviations, speaker markers, and gaps longer than four seconds. Missing punctuation or unusual abbreviations can still produce imperfect sentence boundaries.

There is a 40-second request timeout, a limit of 30 lookups per user per minute, and at most four concurrent lookups per server. Subtitle data is cached for five minutes, successful model results for six hours, and saveable lookup snapshots for thirty minutes. Cache keys include the contextual meaning, model, and prompt version. Cached results are bounded and stored in memory.

## Supported playback

Language learning, its settings, and the vocabulary page are available only in the web desktop and mobile layouts. TV layout hides the settings and navigation, ignores any enabled learning preference shared by the account, and redirects direct vocabulary URLs to the home page. Swiftfin is not modified. Existing playback and subtitle APIs are unchanged; other clients do not use the new optional language learning endpoints.

The feature applies to the browser HTML video player, including fullscreen. SRT/WebVTT and extractable ASS/SSA English text tracks are rendered as interactive plain text in learning mode, using the user's subtitle appearance settings. ASS positioning and animation are not retained in that mode. PGS/VobSub, burned-in subtitles, native mobile fullscreen controls, remote players, and casting do not expose clickable subtitle words.

## API

All routes require a Jellyfin user session. User ownership comes from the authenticated session.

- `POST /LanguageLearning/Lookup`: `ItemId`, `MediaSourceId`, `SubtitleStreamIndex`, zero-based `CueIndex`, UTF-16 `WordOffset` in normalized cue text, `Word`, and `Level` (`0` word, `1` sentence, `2` previous sentences).
- `POST /LanguageLearning/Vocabulary`: `LookupId` from a recent lookup. Idempotently saves its verified context and generated explanation. Expanded lookups can also update a saved sentence translation.
- `GET /LanguageLearning/Vocabulary?startIndex=0&limit=50&searchTerm=word`: paginated, searchable list of the current user's entries, newest first (maximum page size 100).
- `POST /LanguageLearning/Vocabulary/{entryId}/Translation`: translates and saves a missing Ukrainian sentence translation using the original stored context. Requires ownership of the entry; the original media does not need to exist.
- `DELETE /LanguageLearning/Vocabulary/{entryId}`: removes the current user's entry; idempotent.

The database migration creates `UserVocabularyEntries` on the next normal server migration. The vocabulary page is available at `#/vocabulary` in both web layouts.
