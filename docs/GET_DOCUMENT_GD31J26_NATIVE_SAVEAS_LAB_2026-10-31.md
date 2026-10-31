# GD-31J.26 — отдельный native SaveAs по штатным событиям EuroOffice SDK (только синтетическая лаборатория)

**31.10.2026 · База GD-31J.25 / PR #111 · исходники подготовлены, сборка и тесты не исполнялись.**

## Проверенные интерфейсы закреплённого SDK

В commit `Euro-Office/desktop-sdk@8cc8d747cb48953bf8020c2bfbb67ede6cb55c51`:

- `ChromiumBasedEditors/lib/src/cefview.cpp`: JS-команда save с `saveas=true` может инициировать `ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE` и передать `CAscLocalSaveFileDialog` с исходным `FileType`.
- Возврат через `CCefView::Apply(new CAscMenuEvent(ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_SAVE_PATH))` с `CAscLocalSaveFileDialog::put_Path/put_FileType` вызывает `LocalFile_SaveStart(output, type)`.
- `LocalFile_SaveEnd` может посылать `ASC_MENU_EVENT_TYPE_CEF_ONSAVE`, но тот же тип встречается в иных ветках и не является доказательством корректного изменения OOXML.
- Патч GD-31J.24 создаёт `ASC_MENU_EVENT_TYPE_GETDOC_DOCUMENT_CONTENT_READY` в строго обозначенной disposable SDK-копии. Это **вспомогательная нотификация**, не подтверждение импорта.

Здесь **не выполняется произвольный JS/CEF eval** и нет новой команды сохранения из сетевого/GET UI. Режим ждёт, когда оператор вручную запросит **Save As** в *настоящем native editor GUI*, и обслуживает единственный результативный запрос через официальный SDK event + `Apply`.

## Реализовано

1. `GETDOC_GD31J26_ENABLE_NATIVE_SAVEAS_LAB=OFF` по умолчанию. Включение возможно только вместе с GD31J24 (включая все транзитивные требования GD31J23/GD31J22, два утверждённых SHA-256 rebuilt SDK .so и точный проверенный overlay), на Linux/Wayland и после `GETDOC_GD31J26_PRIVATE_OUTPUT_ISOLATION_CONFIRMED=ON` **в дополнительной одобренной VM**. Эта переменная — только операторское подтверждение, не OS sandbox.
2. Новая CLI-команда с прежними строгими аргументами `--open-staged-native-saveas-lab <existing-approved-staging> <one-of-three-fixtures> <session-guid-N> <tab-guid-N>`. Без compile flag команда немедленно отклоняется. Прежний normal launcher/installer без изменений.
3. После проверки штатного GD31J6 manifest исходный синтетический документ **копируется в персональный `QTemporaryDir`**, расположенный внутри `gd31j25-output`; EuroOffice открывает **только временную рабочую копию**. Обычный Ctrl+S не может перезаписать путь исходника, переданный исключительно алгоритму предварительного контроля. Ошибки/доступы SDK из-за отсутствия OS sandbox остаются риском, поэтому настоящий исходник и вся corporate FS должны быть смонтированы read-only или не смонтированы.
4. Каталог `gd31j25-output` должен существовать заранее, быть без символических ссылок, не допускать запись группы/остальных пользователей и содержать точный `gd31j25-output-lab.marker`. Выходные имена определяет только `NativeOutputBasename`, не ввод с клавиатуры, диалога или запроса. При существующем имени целевого результата функция отказывает.
5. `SyntheticEventObserver` обрабатывает `CEF_LOCALFILE_SAVE` **только от точного ID созданного native editor**. Сама обработка callback хранит лишь атомарные флаги/идентификаторы/число запросов. Через `QTimer` на Qt owner thread, **только при** ContentReady, CEF OnLoaded, binding, 2 замерах Editor.bin, modify-событии, единственном запросе, совпадении входного и желаемого `FileType`, отсутствии прежнего output и чужих ссылок, отправляется одно `CEF_LOCALFILE_SAVE_PATH` посредством SDK `Apply`. Никаких auto-save без пользовательского Save As, буфера обмена, экспорта PDF или печати.
6. `CEF_ONSAVE` регистрируется **только после** отправленного разрешённого пути, отмена отдельно помечается. По истечении 90 секунд закрывается GUI/CEF (только ограниченное ручное испытание), после чего анализируется файл из **приватного QTemporaryDir**, независимо перепроверяется источник SHA. В случае подтверждённого callback, отсутствия отмены и неизменного source применяется POSIX `link(oldPrivateOutput, newFinalOutput)`: атомарная публикация **только если конечного имени ещё нет**. Это не `rename` с заменой, не `QFile::copy` в заранее существующий путь. При несогласованном хеше нового hardlink удаляется только он; служебные документы не удаляются.
7. Короткая одна native JSON-квитанция `getdoc-native-saveas-candidate-lab/1` (session, tab, fixture, request, answer, callback, cancel, source unchanged, published output SHA, без путей и текста документа). `nativeSaveProven=false`, `documentVerified=false`, `saveVerified=false`, `pdfVerified=false`, `printApproved=false`, `approvedForProduction=false` всегда.
8. Fail-closed `EuroOfficeNativeSaveAsCandidate.Parse` с дубли/чужими полями, типами, корреляцией флагов, буквальным session/tab, bounded SHA; MSTest сценарии spoofing. Python source contracts и offline local acceptance; **все испытания отложены**, GitHub Actions не включены.

## Порядок будущего локального испытания

- Использовать отдельный disposable Linux Wayland VM без интернета, SMB, GUP-2, WORM, сервисных токенов, пользовательских документов или административных ключей. Подготовить точные локальные исходники/две библиотеки из patched SDK и host executable, затем независимо проверить SHA каждого из них; отдельно рассмотреть AGPLv3 и дополнительные условия EuroOffice.
- Подготовить существующие синтетические GD31J6 DOCX/XLSX/PPTX и manifest. Создать `gd31j25-output/` с marker из GD31J25, закрыть запись другим пользователям; **убедиться, что никакого файла `gd31j25_*_saved.*` ещё нет**.
- Запустить isolated build `getdoc-eurooffice-cef-staged-open-lab --open-staged-native-saveas-lab <staging> gd31j6_writer.docx <sessionN> <tabN>`. Должен появиться настоящий Qt/CEF редактор. В нём вручную изменить текст, ввести **только синтетическую** метку `GD31J25_SYNTHETIC_WRITER_EDIT_6A9B`, вручную нажать **Save As**, дождаться native completion (не интерпретировать просто нажатие как успех).
- Кандидат output `gd31j25-output/gd31j25_writer_saved.docx` публикуется только при наличии одной цепочки событий; проверить, что исходный hash не изменился. Отдельно запустить read-only GD31J25:
  ```sh
  python3 probes/eurooffice-desktop/cef_lab/synthetic_roundtrip.py \
    --staging /ABSOLUTE/APPROVED/DISPOSABLE/STAGING --fixture gd31j6_writer.docx
  ```
- Повторить для Calc и Impress с соответствующими разрешёнными видимыми метками. Для Calc удостовериться, что метка находится в значении ячейки и экспортирована как `s:t`, для Impress — в `a:t` слайда. Повторно открыть опубликованные файлы через независимый редактор, проверить форматы и видимость изменений.
- Негативные ручные проверки: обычный Ctrl+S не затрагивает original; появление конечного имени до записи блокирует публикацию; 2+ Save As request; отмена; отсутствие ContentReady/modified/binding; неправильный FileType; CEF crash; несоответствие исходного SHA; появление ссылки или другого output; отключённый флаг CMake; неправильный SDK SHA/overlay; попытка обычного запуска. При каждом сбое — никаких производственных разрешений и никакого overwrite существующего результата.
- Только после ручной VM приёмки можно рассматривать отдельное объединение **коррелированных native событий, файлового SHA и независимого GD31J25 OOXML/canary verdict**. Сейчас оба типа квитанций **не образуют автоматическое доказательство рабочего редактирования**.

## Известные ограничения

- Пользователь должен вручную инициировать Save As в лабораторной CEF GUI; **нет подтверждённого автоматического редактирования или валидированного полноценного native Office API**.
- Отдельный режим может записывать только *синтетические* временные файлы, но **встроенный Chromium/SDK может быть без OS sandbox**. Marker, пути, hash и CMake-флаги сами по себе не заменяют изоляцию VM, безопасный mount namespace, MAC и политику прав ОС.
- Непрозрачные фоновые задачи SDK могут требовать дополнительных проверок после GUI shutdown. Файл, прошедший атомарную публикацию, остаётся **кандидатом**, пока GD31J25 не подтвердит ZIP/CRC/relationships/canary и оператор не сверит изменения и весь runtime.
- Флаги версий и статусы намеренно запрещают использование для реальных редакций, печати, совместной работы, WORM, SMB, пользовательских документов, лицензируемой поставки. LibreOffice/UNO fallback и фирменная GET 4.1 оболочка не изменены.
- **Компиляция, Qt/C++ и .NET тесты не запускались** по решению об отложенных проверках. PR остаётся Draft, не включён в `main`.
