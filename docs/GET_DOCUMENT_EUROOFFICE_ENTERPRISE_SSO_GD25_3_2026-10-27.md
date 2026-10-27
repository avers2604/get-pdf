# GD-25.3 — блокировка запуска Euro-Office без подтверждённого корпоративного SSO

Дата: 27.10.2026. Draft, редактор корпоративных документов выключен.

## Код

`EuroOfficeEnterprisePrincipalPolicy.IsApproved` требует ровно одну аутентифицированную identity утверждённой корпоративной схемы; полная цепочка ClaimsPrincipal не может содержать конфликтующие `NameIdentifier`. UUID сотрудника должен быть точным, каноническим lowercase `Guid D`, соответствующим идентификатору GUP-2. Непроверенные `X-User` или `X-Forwarded-User` не обладают полномочиями.

`GetDocument.EuroOfficeHost/Program.cs` содержит отдельный флаг `GET_EUROOFFICE_ENTERPRISE_LAUNCH_ENABLED` (default 0). Когда он выключен — launch возвращает 404. Если администратор пытается включить его без реально внедрённого SSO middleware — приложение **останавливает запуск с ошибкой конфигурации**, а не принимает произвольный username claim. Отдельная строка `GET_EUROOFFICE_VALIDATED_SSO_SCHEME` не заменяет подтверждённую регистрацию OIDC/SSO обработчика.

MSTest `EuroOfficeEnterprisePrincipalPolicyTests` проверяет доверенную identity, header-spoofing, отсутствие схемы, анонимность, неправильный UUID, две конфликтующие identity и повторенный principal.

## Следующий реальный блок

Для работающего SSO необходимо выбрать и закрепить серверный провайдер OIDC/AD (Keycloak/authentik), настроить корпоративный CA, получение и валидацию JWKS, issuer, audience, сроков JWT и отзыв сессии, а также подтвердить mapping `sub` в неизменяемый UUID сотрудника GUP-2. После этого можно реализовать `ITrustedEuroOfficeLaunchStore`, использующий подписанный GD-25 HMAC redeem, но без вывода server-only save grant в браузер.

До этих изменений отсутствие реальных Document Server content URL и callback sink сохраняется; настоящий редактор по-прежнему не может получить доступ к рабочей копии. Нельзя создавать фиктивные корпоративные identity, включать production-флаг или считать E2E выполненным.

Локальная приёмка: `dotnet build GetDocument.slnx -c Release`, `dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release`, тесты реального IdP issuer mismatch, audience mismatch, JWKS rotation, logout/revoke, MFA policy, session fixation и несколько рабочих мест. **Не запускались** в текущей среде. GitHub Actions не запускать, PR оставить Draft.
