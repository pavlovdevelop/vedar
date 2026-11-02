# Ръководство за разработчици

## Необходими инструменти

| Инструмент | Нужен за | Бележка |
|---|---|---|
| .NET SDK 10.0 (10.0.100+) | build, тестове | https://dot.net |
| PowerShell 7 (`pwsh`) или Windows PowerShell 5.1 | build скриптовете | на Linux: `dotnet tool install -g PowerShell` |
| Inno Setup 6 | инсталатора | по избор — скриптът сам изтегля проверена версия, ако липсва |
| Wine (само Linux/macOS) | компилиране на инсталатора | `apt install wine`; ISCC.exe работи под Wine |
| `cabextract` (само Linux/macOS) | извличане на VC++ Runtime | под Windows се използва `expand.exe` или System32 |

Visual Studio 2022+/Rider са удобни, но не са задължителни. Решението е `Vedar.sln`.

## Структура

```
src/            8 проекта (виж ARCHITECTURE.md)
tests/          Vedar.Tests (net10.0 + net10.0-windows)
tools/          Vedar.Tools (бенчмаркове, подготовка на модела), branding (генериране на икони)
build/          build.ps1, fetch-models.ps1, publish.ps1, create-installer.ps1, test-installer.ps1, dependencies.json
installer/      VEDAR.iss (Inno Setup) и информационна страница
assets/branding лого, икона, изображения за инсталатора
models/         изтеглените модели (не са в Git)
artifacts/      изход от publish (не е в Git)
dist/           инсталаторът (не е в Git)
docs/           допълнителна документация
```

## Build

Пълен процес (модели → компилиране → тестове → publish → инсталатор):

```powershell
pwsh build/build.ps1
```

Резултат: `dist/VEDAR-Setup-v0.1.0-x64.exe` и `.sha256` файл до него.

Отделни стъпки:

```powershell
pwsh build/fetch-models.ps1        # ~300 MB, SHA-256 проверка, подготовка на гласа
dotnet build Vedar.sln -c Release
pwsh build/publish.ps1             # artifacts/publish/win-x64 (self-contained, ReadyToRun)
pwsh build/create-installer.ps1    # dist/VEDAR-Setup-v0.1.0-x64.exe
pwsh build/test-installer.ps1      # само Windows: тиха инсталация → старт → изход → деинсталация
```

Опции на `build.ps1`: `-SkipTests`, `-SkipInstaller`, `-SkipModels`, `-Configuration Debug`.

Под Linux build-ът е кръстосан (EnableWindowsTargeting): WPF се компилира, а инсталаторът се създава с
Inno Setup под Wine. Приложението не може да се стартира под Linux.

## Стартиране при разработка

Под Windows: `dotnet run --project src/Vedar.Desktop`. Моделите се намират автоматично в папка `models`
на хранилището (търсене нагоре от `bin`). Полезни променливи на средата:

* `VEDAR_DATA_DIR` — отделна папка за настройки/дневници (не пипа истинските данни);
* `VEDAR_MODELS_DIR` — друга папка с модели.

Аргументи на `VEDAR.exe`: `--background` (без прозорец, без отнемане на фокуса), `--exit` (затваря
работещия екземпляр).

## Тестове

```powershell
dotnet test --project tests/Vedar.Tests/Vedar.Tests.csproj -c Release -f net10.0          # Linux и Windows
dotnet test --project tests/Vedar.Tests/Vedar.Tests.csproj -c Release -f net10.0-windows  # само Windows
```

Проектът използва xUnit v3 и Microsoft.Testing.Platform (вижте `global.json`). Тестовият проект е
изпълним файл, така че може да се стартира и директно с филтри:

```bash
dotnet tests/Vedar.Tests/bin/Release/net10.0/Vedar.Tests.dll -class "Vedar.Tests.Core.OrchestratorTests"
dotnet tests/Vedar.Tests/bin/Release/net10.0/Vedar.Tests.dll -method "*Whisper*" -showLiveOutput
```

| Група | Какво проверява | Вид |
|---|---|---|
| Core | числа, дати, машина на състоянията, изречения, разговор, локализация, **оркестратор** (25 сценария) | unit + интеграция с двойници |
| Speech | нормализация, транслитерация, wake word, филтри, VAD, слушател (смяна на микрофон), ONNX метаданни, ускоряване на речта без промяна на тона (WSOLA) | unit |
| Integration | **истински Whisper и Piper**: WAV от Piper, скорост, прекъсване на синтеза, FLEURS, глас → намерение | реални модели |
| AI | SSE, JSON, **mock HTTP сървър (Kestrel)**: стрийминг, повторения, 401/429/5xx, timeout, отказ, прекъсване, без интернет, ключът не попада в дневници, Ollama, Auto | интеграция |
| Commands | разпознаване на ~70 български фрази, въпроси към AI, диспечер, файлове (UTF-8/1251/UTF-16/docx), безопасност | unit |
| Infrastructure | настройки, маскиране на тайни, autostart, изтегляне с проверка, изтриване на данни, политика за екранен четец | unit + интеграция |
| Build | инсталаторен скрипт, закачени зависимости, версии, **няма API ключове в хранилището**, реалният publish и инсталатор | проверка на артефакти |
| Windows | **реален DPAPI**, **реален регистър (HKCU Run)**, клавиши, WPF одит на достъпността и Tab реда, **FlaUI UI Automation върху VEDAR.exe** | само Windows |

Тестовете с реални модели се пропускат с ясно съобщение, ако моделите не са изтеглени.

## Бенчмаркове

```bash
dotnet run --project tools/Vedar.Tools -c Release -- bench-commands vocab
dotnet run --project tools/Vedar.Tools -c Release -- bench-wakedetect ggml-base-q5_1.bin 768
dotnet run --project tools/Vedar.Tools -c Release -- bench-wake <папка с WAV>
dotnet run --project tools/Vedar.Tools -c Release -- bench-rate 5
dotnet run --project tools/Vedar.Tools -c Release -- synthesize "Здравей!" out.wav 1.5
dotnet run --project tools/Vedar.Tools -c Release -- transcribe out.wav
```

Аргумент `@файл.txt` чете текста от UTF-8 файл. Резултати: [docs/SPEECH_BENCHMARKS.md](docs/SPEECH_BENCHMARKS.md).

## Как да добавите нова гласова команда

1. В `src/Vedar.Commands/CommandDispatcher.cs`, метод `BuildRecognizer`, добавете шаблон:
   `.Add("myIntent", "отвори|пусни калкулатора|калкулатор", 1)`
   (`|` — алтернативи, `?` — незадължителна дума, `{rest}`/`{n}` — слотове, последното число — колко
   „излишни“ думи се допускат).
2. В `ExecuteAsync` добавете `case "myIntent":` с действието. Ако е нужно ново системно действие,
   добавете метод в `ISystemActions` (Core) и в `WindowsSystemActions` — **никога** не изпълнявайте
   произволни команди и не пускайте процеси с аргументи от потребителя или от AI.
3. Чувствителните действия връщат `CommandResult.Confirm(...)`.
4. Добавете текстовете в `src/Vedar.Core/Localization/bg.json`.
5. Добавете фрази в `IntentRecognitionTests` (включително варианти с грешки на Whisper) и тест в
   `CommandDispatcherTests`.

## Смяна на компоненти

* **Глас:** реализирайте `ISpeechSynthesizer` (Core) и го свържете в `src/Vedar.Desktop/Hosting/AppServices.cs`.
  Друг глас на Piper: копирайте `<глас>.onnx` и `<глас>.onnx.json` в `models/piper/<глас>/` и задайте
  `Voice.VoiceId` в настройките (за не-български гласове е нужен съответният `espeak-ng-data` език).
* **Разпознаване:** `ISpeechRecognizer`. Друг модел на Whisper — изберете го в настройките или го
  добавете в `ModelCatalog`.
* **AI:** реализирайте `IChatProvider` (Vedar.AI) и го добавете в `AiRouter`.

## Език на интерфейса

Текстовете са в `src/Vedar.Core/Localization/<език>.json` (вграден ресурс). Липсващите ключове се взимат
от `bg.json`. Нов език: добавете файла и кода му в `Localizer.Languages`.

## Издаване на нова версия

1. Сменете версията на 4 места: `Directory.Build.props` (`VedarVersion`), `build/common.ps1`,
   `installer/VEDAR.iss` (`AppVersion`), `src/Vedar.Desktop/app.manifest`. Тестът
   `Version_is_consistent_everywhere` проверява съвпадението. Не сменяйте `AppId` в `VEDAR.iss`.
2. Обновете `CHANGELOG.md`.
3. `pwsh build/build.ps1`, после `pwsh build/test-installer.ps1` на Windows.
4. Препоръчително: подпишете `VEDAR.exe` и инсталатора с код-подписващ сертификат (премахва
   предупреждението на SmartScreen).
5. Публикувайте инсталатора заедно с `.sha256` файла.

## CI

`.github/workflows/ci.yml`:

* **Linux:** модели (кеширани) → build → всички net10.0 тестове с реалните модели.
* **Windows (windows-2025):** модели → build → publish → net10.0 и net10.0-windows тестове
  (DPAPI, регистър, WPF/UIA, FlaUI) → инсталатор → тиха инсталация/старт/изход/деинсталация →
  качване на инсталатора като artifact.

## Правила

* Никакви тайни в кода, настройките или дневниците (тест `Repository_contains_no_api_keys_or_secret_files`).
* Без телеметрия и без мрежови заявки извън BgGPT/Ollama (по избор на потребителя) и изтеглянето на модели.
* Всеки нов UI елемент: `AutomationProperties.Name`, достъпен с Tab, уникален access key в прозореца.
  Тестът `KeyboardAccessibilityTests` проверява това.
