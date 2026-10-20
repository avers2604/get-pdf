# ГЭТ PDF — Windows offline build and MSI gate

`scripts/windows-build-msi.ps1` выполняется **только на Windows** в подготовленном стенде. Требуются .NET SDK 10, WiX Toolset 5 и доступ к проверенным локальным NuGet-пакетам. Автоматические GitHub Actions не используются.

```powershell
pwsh -File .\scripts\windows-build-msi.ps1 -OfflineNuGetFeed 'D:\ApprovedNuGet'
```

Сценарий последовательно выполняет restore, build, MSTest, удаление старой папки publish, самостоятельную публикацию для win-x64 и проверку на ошибочно попавшие scanner/driver worker. WiX собирает отдельный per-machine MSI; регистрируется **только доступность ГЭТ PDF для .pdf**, не изменяется UserChoice. Отдельный UpgradeCode предотвращает конфликт с GUP-Scan.

Подпись необязательна при исследовательской сборке, но для выпуска нужна подтверждённая цепочка доверия. Для внутреннего сертификата:
```powershell
pwsh -File .\scripts\windows-build-msi.ps1 -OfflineNuGetFeed 'D:\ApprovedNuGet' -SignToolPath 'C:\SDK\signtool.exe' -SigningThumbprint 'CERTIFICATE_SHA1_THUMBPRINT'
```

**Статус:** сценарий и WiX-описание добавлены в исходники, но в текущем окружении НЕ исполнялись. Факт успешной Windows-сборки, MSBuild/WiX совместимость, установка/обновление/удаление MSI, запуск PDF, подписи и печать ещё не подтверждены. `release-manifest.json` формируется только после успешного локального gate и не является актом аппаратной приёмки.

Проверить дополнительно: EXE/desktop runtime и целостность всего payload, создание ярлыка, ассоциацию «Открыть с помощью», сохранение выбора пользователя, установку и удаление рядом с GUP-Scan, x64 и фактическое время печати. Сервисов и доступа к интернету приложение не требует.

**Важно:** установщик `ГЭТ PDF` не имеет права удалять драйверы сканера, профили GUP-Scan или документы пользователя.
