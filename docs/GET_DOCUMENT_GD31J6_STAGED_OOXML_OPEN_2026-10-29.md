# GD-31J.6 — переход от пустого QCefView к OpenLocalFile для трёх тестовых форматов

**Дата:** 29 октября 2026. **Статус:** реализация в отдельной Draft-ветке. По решению пользователя **сборку и лабораторные проверки откладываем**. Никаких заявлений, что реальный документ уже открыт в скомпилированном приложении.

## Назначение

Этот этап добавляет первый **реальный вызов офисного редактора EuroOffice** поверх GD-31J.4:

```cpp
view->Create(&manager, cvwtEditor);
const int format = CCefViewEditor::GetFileFormat(path.toStdWString());
auto* editor = static_cast<CCefViewEditor*>(view->GetCefView());
editor->OpenLocalFile(path.toStdWString(), format, L"");
```

Маршрут подтверждён в исходниках закреплённого `Euro-Office/desktop-apps@7a1297b3172a0993401308fbd09633e0597704e0`, `win-linux/src/components/ctabpanel.cpp` (метод `CTabPanel::openLocalFile`). `QCefView::Create(cvwtEditor)` сам создаёт редактор. Это **не** второй прямой вызов `CreateCefEditor`.

Учитывая существующий контракт ГЭТ, **не** меняются основной локальный редактор LibreOffice/UNO, единое окно Avalonia, собственный мастер печати и серверная архитектура EuroOffice DocumentServer. Этот эксперимент **не включён** в пользовательский запуск или установщик.

## Добавлено в код

### 1. Синтетические исходные файлы

`probes/eurooffice-desktop/prepare_office_fixtures.py` — полностью офлайн, только Python stdlib/zipfile. Создаёт в **новом пустом локальном каталоге**:

| Имя | Назначение |
| --- | --- |
| `gd31j6_writer.docx` | текстовый документ с заголовком на русском |
| `gd31j6_calc.xlsx` | таблица с текстом и простой формулой |
| `gd31j6_impress.pptx` | презентация с одним тестовым слайдом |

Для каждого ZIP/OOXML образца создаётся `gd31j6-fixture-manifest.json` с точным размером и SHA-256. **Не** используются Word, Excel, PowerPoint, внешние сервисы, клиентские файлы или сервер. Генератор отказывает, если в каталоге уже что-либо лежит, и не перезаписывает данные. Само наличие таких минимальных OOXML файлов **не гарантирует корректный импорт EuroOffice**; это отдельная будущая проверка.

### 2. Реальный нативный путь открытия

`probes/eurooffice-desktop/cef_lab/cef_staged_open.cpp`:
- принимает только `--open-staged-lab <абсолютный-локальный-каталог> <один-из-3-известных-файлов>`;
- сверяет существование manifest, его схему и назначение, ровно 3 файла, размер каждого передаваемого файла до 128 MiB, полное SHA-256 совпадение перед передачей его в CEF;
- запрещает URL, UNC/сетевые пути, ссылки/symlink в пути, переход за staging root, макросные форматы и произвольный файл;
- создаёт отдельный временный CEF profile, включает один `QCefView`, определяет формат через `CCefViewEditor::GetFileFormat`, затем вызывает **реальный** `OpenLocalFile`;
- ограничивает период пробного отображения 15 сек, затем инициирует штатное закрытие через `DestroyCefView` и `CloseApplication`;
- выводит **только** `GD31J6_OPEN_REQUEST_DISPATCHED_NOT_VERIFIED`: это означает вызов SDK, **не** подтверждает загрузку содержимого, корректность визуала, полноту XLSX/PPTX, сохранение, аудит, WORM/CAS или передачу PDF.

Прямой вызов печати, макросов, экспорт в PDF, редактирование оригинала и интеграция с Nextcloud/GUP-2 **не реализованы**. Нативный CEF теоретически может записать временную тестовую копию; никогда не передавать настоящие исходные документы в этот эксперимент.

### 3. Независимая опция компиляции

`cef_lab/CMakeLists.txt`: `GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB=OFF` по умолчанию. Включается **только если** уже выбраны `GETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB=ON`, `GETDOC_GD31J3_ENABLE_CEF_LAB=ON`, `GETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED=ON`, закреплены исходные Git SHA и доступны предварительно подготовленные локальные Qt6/CEF/SDK библиотеки. Никакого network download или GitHub Actions. **Скрипт не меняет исполняемые файлы ГЭТ**.

## Отложенная последовательность локальной лаборатории

_Не выполнялась здесь и не требуется для следующего этапа написания кода._

Сначала на специально подготовленной офлайн-VM без доступных служебных данных:

```bash
python probes/eurooffice-desktop/prepare_office_fixtures.py --out /tmp/gd31j6-fixtures
```

Затем сборка на отдельной разрешённой машине (все зависимости должны быть заранее предоставлены, включая ресурсные файлы CEF и редактора):

```bash
cmake -S probes/eurooffice-desktop/cef_lab -B artifacts/gd31j6 \
  -DGETDOC_GD31J3_ENABLE_CEF_LAB=ON \
  -DGETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB=ON \
  -DGETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB=ON \
  -DGETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED=ON \
  -DEUROOFFICE_DESKTOP_TREE=/approved/DesktopEditors \
  -DEUROOFFICE_RUNTIME_LIB_DIR=/approved/eurooffice-runtime/lib
cmake --build artifacts/gd31j6 --config Release
```

При дополнительном отдельном одобрении ИБ и ОС-изоляции:

```bash
artifacts/gd31j6/getdoc-eurooffice-cef-staged-open-lab \
  --open-staged-lab /tmp/gd31j6-fixtures gd31j6_writer.docx
```

Повторить вручную `gd31j6_calc.xlsx` / `gd31j6_impress.pptx`. На Windows — абсолютный локальный путь без сетевых папок, фактический путь Release EXE/Qt/CEF runtime.

**Безопасность:** upstream EuroOffice CEF может использовать `no_sandbox=true` при отсутствии `CEF_USE_SANDBOX`. Текстовый флаг `LOCAL_ISOLATION_CONFIRMED`, SHA-256 fixture и временный профиль **не создают песочницу ОС**. Использовать отдельную изолированную VM, без доступа в сеть, к рабочим файлам и паролям. Сверку лицензий AGPLv3/GUI уведомлений и исходников провести перед установкой.

## Следующий кодовый этап GD-31J.7 (без тестовой лаборатории пока)

- Изолировать обработчик событий editor view: `OnLoaded`, ошибки, закрытие, timeout и crash с явно различаемыми состояниями `requested/loaded/failed/closed` — браузерный `OnLoaded` нельзя автоматически трактовать как успешный импорт документа.
- Разработать **только запрос сохранения новой редакции** в staging output с блокировкой перезаписи, WORM/CAS guard. Фактическую запись/обработку событий привязать лишь к подтверждённому upstream API.
- Для мастера печати — отдельный запрос `export-pdf` во временный PDF, а не штатную кнопку прямой печати EuroOffice.
- Доработать процессный IPC между GET оболочкой Avalonia и изолированным Qt host, сохранив единое окно пользователя; не внедрять сырой `QWidget*` или Qt в процесс Avalonia.

## Контроль готовности

| Компонент | Статус |
| --- | --- |
| Генератор DOCX/XLSX/PPTX и manifest | Код добавлен |
| `GetFileFormat` → `OpenLocalFile` | Вызов в C++ коде |
| Отдельная опция CMake | Добавлена, OFF по умолчанию |
| Реальная загрузка файлов в Qt/CEF | **Не проверена** |
| Просмотр/редактирование документов | **Не подтверждены** |
| Сохранение, PDF export, мастер печати | **Не реализованы в новом нативном engine** |
| Встраивание в единое окно ГЭТ | **Не реализовано** |
| Сборка/тесты (по решению пользователя позже) | **Отложены** |

Основная ветка `main` и GitHub Actions не меняются, PR должен оставаться Draft.
