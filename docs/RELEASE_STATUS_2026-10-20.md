# ГЭТ PDF — принятые/непринятые возможности по выпуску

| Функция | Статус реализации | Что обязательно подтвердить на Windows |
|---|---|---|
| Много PDF во вкладках | Код есть | WPF memory, закрытие и черновики |
| Выделение текста | Слова одной текущей страницы | Кириллица, координаты, сканы; посимвольный выбор ещё не реализован |
| Заполнение AcroForm | Простые текстовые поля; signed/XFA/JS отказ | Шрифты, appearance, сохранение русских букв на внешних PDF-reader |
| Проверка ЭП | Локальная криптография + локальная цепочка | КЭП, ГOCT/CryptoPro, timestamp, OCSP/CRL, отказ доверия |
| PDF print preview | Raster spool, профиль драйвера A3/A4, duplex, copies | МФУ, физическая печать; векторный backend отсутствует |
| Производительность | 2 одновременных native импортера, 32 МиБ bitmap кэша на вкладку | Native peak/CPU на PDF до 1000 стр. |
| MSI | Source WiX5 и offline PowerShell gate | Windows build, MSTest, install/upgrade/remove, Authenticode |
| Обновления | Read-only offline MSI preflight с SHA-256 и Authenticode; установка не производится | Проверить PowerShell/WindowsInstaller COM, PKI, реальный rollback |
| GET-system | Опциональный локальный JSON v1, отключён по умолчанию | Согласовать reader/ACL и будущий API интеграции на стенде |
| GUP-Scan | Отдельный репозиторий без изменений | Сохранить экспорт сканов в PDF при будущей миграции |

Это **не** протокол успешной приёмки. PR остаётся Draft, пока реальное оборудование и Windows gate не проверены.
