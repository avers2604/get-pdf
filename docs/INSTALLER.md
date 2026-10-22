# ГЭТ PDF — установщик (MSI) и приложение по умолчанию

## Сборка

Требуется Windows x64, .NET SDK 10 и WiX Toolset 5 с расширениями UI/Util (один раз на машину):

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2 WixToolset.Util.wixext/5.0.2
.\scripts\windows-build-msi.ps1
```

Скрипт выполняет restore → build → MSTest (порог 185) → чистый self-contained publish →
`wix build` (`-culture ru-RU`) и пишет `dist\release\GetPdf-0.1.1-win-x64.msi` и `release-manifest.json`.

## Что делает установщик

- Устанавливает приложение для всех пользователей в `C:\Program Files\GET PDF` (perMachine,
  требует прав администратора) и создаёт ярлык «ГЭТ PDF» в меню «Пуск».
- Мастер на русском языке: приветствие → папка установки → установка → завершение.
- Регистрирует ГЭТ PDF как обработчик PDF, но **не меняет выбор пользователя**:
  - ProgID `GetPdf.PDF` (иконка, команда открытия);
  - `.pdf\OpenWithProgids` — пункт в «Открыть с помощью»;
  - `Applications\GetPdf.exe` (`FriendlyAppName`, `SupportedTypes`);
  - `Software\GetPdf\Capabilities` + `RegisteredApplications\GetPdf` — запись в «Параметры › Приложения › Приложения по умолчанию»;
  - `App Paths\GetPdf.exe`.
- На последней странице включён флажок «Открыть „Приложения по умолчанию“…». На Windows 11 22H2+
  он открывает страницу ГЭТ PDF (`ms-settings:defaultapps?registeredAppMachine=GetPdf`), на более
  старых версиях — общую страницу «Приложения по умолчанию».
- Иконка программы — инверсный знак ГЭТ из брендбука на корпоративном тёмно-синем
  (`src/GetPdf/Assets/get-pdf.ico`, из `get-mark-inverse.png`): читается и на светлой, и на тёмной панели задач.
- Иконка PDF-файлов отдельная: лист с загнутым углом, красная плашка «PDF» и маленький знак ГЭТ
  (`pdf-document.ico` рядом с exe, на неё ссылается ProgID).
- Версия 0.1.1 ставится поверх 0.1.0 (MajorUpgrade).

## Как сделать ГЭТ PDF приложением по умолчанию

Windows 10/11 защищает выбор приложения по умолчанию (`UserChoice`): установщик технически может
только зарегистрировать ГЭТ PDF и предложить выбор.

- **Пользователь:** на последней странице мастера оставить флажок → в открывшихся параметрах
  нажать строку `.pdf` → выбрать «ГЭТ PDF». Другой путь: ПКМ по PDF › «Открыть с помощью» ›
  «Выбрать другое приложение» › «ГЭТ PDF» › «Всегда».
- **ИТ-служба, для всех пользователей домена:** после установки MSI применить
  `packaging/DefaultAppAssociations.xml` через GPO «Set a default associations configuration file»
  или командой `Dism /Online /Import-DefaultAppAssociations:…` при подготовке образа.

Тихая установка для развёртывания (без мастера и без открытия параметров):

```bat
msiexec /i GetPdf-0.1.1-win-x64.msi /qn /l*v getpdf-install.log
```

Удаление: «Параметры › Приложения» или `msiexec /x GetPdf-0.1.1-win-x64.msi /qn`.
Регистрация удаляется вместе с программой.

## Проверено (22.10.2026, Windows 11, 125 % DPI)

- `windows-build-msi.ps1` целиком: 181/181 тестов, MSI 66 МБ, 492 файла.
- Установка `/passive` с кодом 0. Есть файлы, ярлык «Пуск» и запись в «Приложениях»;
  все ключи реестра на месте; ГЭТ PDF появился в `OpenWithProgids` рядом с другими PDF-программами;
  текущий выбор пользователя не изменён.
- Установленная копия запускается командой, зарегистрированной для ProgID, и открывает PDF. Иконка — официальный знак.
- `ms-settings:defaultapps?registeredAppMachine=GetPdf` открывает «Приложения по умолчанию › ГЭТ PDF» с типом `.pdf`.

Не проверено: интерактивный мастер с кликами по страницам, обновление поверх предыдущей версии
(MajorUpgrade), удаление, применение `DefaultAppAssociations.xml` через GPO/DISM,
Authenticode-подпись MSI.

## Необязательные компоненты, которые ставит ИТ-служба

- **Сжатие PDF (qpdf):** `tools\qpdf\qpdf.exe` и утверждённый `qpdf.sha256` рядом с GetPdf.exe.
  Без них команда «Сжать PDF без потери качества…» скрыта.
- **OCR:** `tessdata\rus.traineddata` и `tessdata\eng.traineddata` рядом с GetPdf.exe.
  Без них команда «Распознать текст (OCR)…» скрыта.
