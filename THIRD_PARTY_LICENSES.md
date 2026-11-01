# Лицензи на използвани компоненти (Third-party licenses)

VEDAR се разпространява безплатно под лиценза **GNU General Public License, версия 3 или по-нова
(GPL-3.0-or-later)** — вижте файла [LICENSE](LICENSE).

## Защо GPL-3.0?

Българският глас на Piper има нужда от фонемизатора **espeak-ng**, който е под лиценз GPL-3.0-or-later.
espeak-ng е статично вграден в библиотеката `sherpa-onnx-c-api.dll`, която VEDAR разпространява. Затова
целият разпространяван пакет трябва да спазва GPL-3.0. Всички останали компоненти (MIT, Apache-2.0, CC0)
са съвместими с GPL-3.0. Изходният код на VEDAR е публичен; изходният код на espeak-ng и sherpa-onnx е
достъпен на адресите по-долу (GPL-3.0, раздел 6).

Библиотеките на Microsoft Visual C++ Runtime са „System Libraries“ по смисъла на GPL-3.0 и се разпространяват
по условията на Microsoft (вижте по-долу).

## Компоненти, включени в инсталатора

| Компонент | Версия | Лиценз | Източник | Употреба |
|---|---|---|---|---|
| .NET Runtime, WPF, Windows Forms | 10.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf | Среда за изпълнение (self-contained) |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet | MVVM |
| Microsoft.Extensions.Logging / DependencyInjection | 10.0.14 | MIT | https://github.com/dotnet/runtime | Дневник |
| System.Security.Cryptography.ProtectedData | 10.0.14 | MIT | https://github.com/dotnet/runtime | Windows DPAPI за ключа |
| NAudio.Core, NAudio.Wasapi | 2.2.1 | MIT | https://github.com/naudio/NAudio | Микрофон и звук (WASAPI) |
| Whisper.net, Whisper.net.Runtime | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net | .NET връзка към whisper.cpp |
| whisper.cpp / ggml | (в Whisper.net 1.9.1) | MIT | https://github.com/ggml-org/whisper.cpp | Локално разпознаване на реч |
| sherpa-onnx (C# API и native runtime) | 1.13.8 | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx | Изпълнение на гласа Piper (VITS) |
| ONNX Runtime | (в sherpa-onnx 1.13.8) | MIT | https://github.com/microsoft/onnxruntime | Невронна мрежа на гласа |
| espeak-ng (вграден в sherpa-onnx) + данни `espeak-ng-data` | 1.52 (fork на k2-fsa/piper) | **GPL-3.0-or-later** | https://github.com/espeak-ng/espeak-ng, https://github.com/k2-fsa/espeak-ng | Фонемизация на българския текст |
| piper-phonemize (вграден в sherpa-onnx) | — | MIT | https://github.com/rhasspy/piper-phonemize | Фонемизация, съвместима с Piper |
| Microsoft Visual C++ 2015–2022 Runtime (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`, `vcomp140.dll`) | 14.44.35211 | Microsoft Software License Terms (Redistributable Code) | https://aka.ms/vs/17/release/vc_redist.x64.exe | Нужни на whisper.cpp; разпространяват се локално към приложението |
| Inno Setup (код на инсталатора и деинсталатора) | 6.7.3 | Inno Setup License (безплатен, вкл. за търговска употреба) | https://jrsoftware.org/isinfo.php | Създаване на инсталатора |

## Речеви модели, включени в инсталатора

| Модел | Лиценз | Източник | Бележки |
|---|---|---|---|
| Whisper `ggml-small-q5_1.bin` | MIT (теглата на OpenAI Whisper) | https://huggingface.co/ggerganov/whisper.cpp | Многоезичен модел, конвертиран за whisper.cpp |
| Whisper `ggml-base-q5_1.bin` | MIT | https://huggingface.co/ggerganov/whisper.cpp | За бързо откриване на думата „Ведар“ |
| Piper глас `bg_BG-dimitar-medium` | Хранилището rhasspy/piper-voices е под MIT; данните на гласа са **CC0** (OHF-Voice voice-datasets) | https://huggingface.co/rhasspy/piper-voices/tree/main/bg/bg_BG/dimitar/medium | Вижте бележката по-долу |

**Бележка за гласа `bg_BG-dimitar-medium`:** според MODEL_CARD гласът е дообучен („finetuned“) от
американския глас `en_US-lessac-medium`, който е обучен върху данните *Lessac Blizzard 2013*
(https://www.cstr.ed.ac.uk/projects/blizzard/2013/lessac_blizzard2013/license.html). Условията на тези данни
не можаха да бъдат прочетени изцяло от средата за разработка. VEDAR се разпространява **безплатно и с
нетърговска цел**. Преди търговско разпространение проверете тези условия; ако се окажат ограничителни,
архитектурата позволява смяна на гласа (вижте DEVELOPER_GUIDE.md, „Смяна на гласа“).

**Промяна на модела:** инсталаторът съдържа файла `bg_BG-dimitar-medium.sherpa.onnx` — копие на оригиналния
модел, към края на който са добавени няколко метаданни (protobuf поле `metadata_props`), нужни на
sherpa-onnx. Теглата на невронната мрежа не са променени. Оригиналът и контролната му сума са описани в
`build/dependencies.json`.

## Само за разработка и тестове (не се включват в инсталатора)

| Компонент | Лиценз | Употреба |
|---|---|---|
| xunit.v3, xunit.runner.visualstudio | Apache-2.0 | Тестове |
| Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.App (Kestrel) | MIT | Тестове, mock HTTP сървър |
| FlaUI.UIA3 | MIT | UI Automation тестове |
| Google FLEURS (5 записа, `tests/Vedar.Tests/TestData/fleurs`) | CC BY 4.0 | Тест с истинска българска реч; виж LICENSE.md в папката |
| Tools.InnoSetup (NuGet опаковка на Inno Setup) | Inno Setup License | Използва се от build скрипта само ако Inno Setup не е инсталиран |

## Външни услуги (не се разпространяват)

* **BgGPT API** (https://models.bggpt.ai/docs/) — облачна услуга на INSAIT. Използва се само ако потребителят
  въведе собствен ключ. Важат условията на BgGPT. VEDAR не е свързан с OpenAI и не използва ChatGPT.
* **Ollama** (https://ollama.com, MIT) — по желание, инсталира се отделно от потребителя.

## Шрифтове и изображения

* Интерфейсът използва системния шрифт Segoe UI на Windows (не се разпространява).
* Логото и надписът VEDAR са собственост на проекта VEDAR. Файловете в `assets/branding` са векторно
  пресъздадени по предоставените от автора изображения.
