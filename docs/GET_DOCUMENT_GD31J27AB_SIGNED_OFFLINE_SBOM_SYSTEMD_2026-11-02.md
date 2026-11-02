# GD-31J.27ab — подписанный офлайн-SBOM и наблюдения эффективной systemd-политики

**Дата:** 2 ноября 2026 года. **Основа:** stacked Draft PR №140 (GD-31J.27aa). **Состояние:** только исходники и отложенные регрессионные тесты. НЕ выполнялись сборка, Python unittest, реальные Ed25519 подписи корпоративным ключом, root-процессы, Astra Linux VM, `systemctl`, ELF loader /proc, реальный SOCK_SEQPACKET/SCM_RIGHTS и SaveAs.

## Область изменений

`native_saveas_offline_sbom_evidence.py` — автономная функция проверки **необходимых** фактов. Нет `main()`, записи файлов, установки пакетов, получения ключа из сети, запуска служб или подключения к CEF, broker listener и worker.

Каталог `getdoc-gd31j27ab-detached-ed25519-offline-sbom-review/1` — ровно один **канонический ASCII JSON + LF**, без неизвестных/повторных полей и NaN, ≤32 КиБ. Содержит:

- `generation` — строго положительный номер версии, проверяемый относительно **независимо закреплённого оператором** ожидаемого значения; без внешнего anti-rollback хранилища версионность не удостоверяется.
- `mode=inspect_only`, `laboratoryOnly=true`, `publishEnabled=false`.
- `modules` — точные SHA-256 четырёх фиксированных локальных модулей предыдущего GD31J27aa.
- `importEdges` — декларируемый граф импортов только этих четырёх модулей, включая пустые допустимые списки. `compare_local_import_graph` проверяет соответствие подписанных SHA фактическим байтам всех четырёх исходников, использует AST-проверку GD31J27aa и требует точного соответствия локальных импортов.
- `wheels` — упорядоченный список до 32 строго именованных wheel-артефактов и SHA-256. **Не** выполняет установку wheel и не доказывает содержимое `RECORD` / wheel signature / transitive dependencies.
- `elf` и `elfNeeded` — точные allowlist пути и SHA-256 `.so` из предыдущего этапа, а также заявленные `DT_NEEDED` soname-зависимости **в рамках конечного allowlist**. Это внешне переданные сведения, не результат запуска реального loader/ELF-парсера. `dlopen` и runtime maps отдельно не удостоверяются.
- `units` — два exact SHA предыдущих образцов `getdoc-gd31j27aa-{broker,worker}.blocked.service` с `ExecStart=/usr/bin/false`.

Поля сверяются с предыдущим `native_saveas_dependency_unit_pins.validate_manifest`: ни один новый модуль, ELF или unit hash не может бесконтрольно заменить старый независимый root-owned pin.

## Реальная криптографическая проверка

`inspect_signed_catalog(raw,signature,public_key,expected_pubkey_sha256,expected_catalog_sha256,expected_generation,legacy_manifest)`:

1. Принимает **detached Ed25519** подпись ровно 64 байта и открытый ключ 32 байта.
2. Проверяет SHA-256 открытого ключа и каталога с помощью `hmac.compare_digest` против **независимо предоставленных доверенных fingerprints**, а также `generation`.
3. Сопоставляет все старые source/ELF/unit digests с GD31J27aa.
4. При наличии отдельно установленного и проверенного пакета `cryptography` вызывает реальный `Ed25519PublicKey.from_public_bytes(public_key).verify(signature,raw)`. Нет fallback, если библиотека отсутствует либо подпись неверна.
5. Возвращает только `detachedSignatureVerified=true` — **не** `signatureAttested`, не разрешение на запуск, публикацию или сохранение.

**Ключ никогда не создаётся в этом PR.** Если fingerprints/public key/catalog поступили по одному и тому же недоверенному каналу, успешная подпись ничего не говорит о корпоративном доверии. Для будущего использования оператор должен заранее закрепить fingerprint в независимом неизменяемом источнике, реализовать anti-rollback и доверенную процедуру обновления. Текущий PR не содержит продукционных ключей и не вводит подпись для корпоративного ПО.

`test_native_saveas_offline_sbom_evidence.py` включает как mock проверку точного вызова криптографии, так и общедоступный эталонный RFC 8032 Ed25519 (при наличии `cryptography`) с отказом при изменении сообщения; проверка не выполнялась.

## Эффективные политики systemd

`inspect_effective_unit_snapshot` требует ровно две внешне собранные записи `broker` и `worker`, соответствующие блокирующим свойствам: `ExecStart=/usr/bin/false`, разные непривилегированные `User`, `NoNewPrivileges=yes`, `PrivateNetwork=yes`, `PrivateMounts=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, пустые capabilities, `UnitFileState=disabled` и отсутствие `DropInPaths`. Дополнительные или ослабленные поля немедленно отклоняются.

Это **не прямой вызов systemd** и не проверка подлинности ответов `systemctl show`: внешняя запись могла быть поддельной, а systemd policy — дополнена генераторами, socket activation, делегированием и динамическими drop-ins. Даже exact snapshot устанавливает только `effectiveUnitFactsObserved=true`, но `effectiveUnitAttested=false`. Реальные службы в репозитории по-прежнему только **заблокированные образцы без секции Install**, не установлены на ПК.

## Отказ в публикации и открытые ограничения

Любой положительный результат имеет `state=requires-independent-review`, а поля `signatureAttested`, `trustedRootManifestAttested`, `dependencyClosureAttested`, `runtimeLibrariesAttested`, `effectiveUnitAttested`, `launchAuthorized`, `nativePublicationAuthorized`, `writePermissionGranted`, `releaseReady`, `approvedForProduction` **всегда false**.

**Самого полного dependency closure пока нет.** AST не гарантирует полноты динамических импортов, стандартная библиотека Python может загружать C extensions, `cryptography` сама имеет binary/ELF зависимости, и `DT_NEEDED` не покрывает `dlopen`. Нужно независимо доказать доступность и неизменность всех transitive wheel/stdlib/.so артефактов, package signatures, runtime maps и того, что эти данные собраны доверенной ОС. Пока это не выполнено, не подключать код к worker или production.

В предыдущем GD31J27aa исправлена тестовая фикстура unit, где `RefuseManualStart` ошибочно находился в `[Service]`: теперь он в `[Unit]` вместе с `ConditionPathExists`. Сам установленный unit не менялся.

## Приёмка и дальнейшие действия

Добавлены `test_native_saveas_offline_sbom_evidence.py`, `test_native_saveas_offline_sbom_contract.py`; две pre-restore записи в `scripts/office_acceptance.py`, проверка в `scripts/tests/test_office_acceptance.py`. В строгой матрице **36 fault cases / 33 mandatory manual E2E gates**; новые `detached-ed25519-sbom-key-rotation-digest-mismatch-unit-dropin-deny` и `eurooffice-gd31j27ab-ed25519-sbom-effective-unit-review-e2e`.

CMake `GETDOC_GD31J27AB_ENABLE_SIGNED_SBOM_PUBLICATION_LAB=OFF`, а любое ON — `GD31J27AB_BLOCKED` / `FATAL_ERROR`, все l–aa запреты сохраняются.

**GD31J27ac:** независимый *offline OS evidence provider* для actual package hashes, wheel RECORD/transitive source and shared-object closure, effective systemd metadata, root-of-trust fingerprint и воспроизводимого sandbox process state. Только после отдельной VM-проверки с реальными kernel fd и ручного security review можно рассматривать синтетический E2E; работоспособный SaveAs на корпоративных файлах по-прежнему не разрешён.

**Не изменялись:** main, GitHub Actions, пользовательские DOCX/XLSX/PPTX/PDF, WORM, GUP-2, Nextcloud, SMB, производственные права и конфигурация ОС.
