# GD-09D — безопасное чтение WOPI discovery

Добавлен разбор официального Collabora `/hosting/discovery` без HTTP-запросов и
без загрузки DTD/внешних сущностей. Выбирается только действие `view`
для DOCX/XLSX/PPTX, URL должен принадлежать заранее разрешённому HTTPS
origin. Поддерживаются элементы `app` с `name` и `action ext`.

Это только **парсер**: HTTP-загрузка discovery, проверка TLS/PKI,
доверенные RSA proof keys, launch POST с токеном, WOPI host HTTP
CheckFileInfo/GetFile, ACL и действующая Collabora отсутствуют.
Редактирование, PutFile, сохранение версий и WORM-аудит не включены.
Окончательный выбор между Collabora, ONLYOFFICE и LibreOffice не сделан.

Добавлены пять MSTest-сценариев, **не выполненных** без .NET SDK.
Требуется локальный `dotnet build GetDocument.slnx -c Release` и
`dotnet test tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj -c Release`.
Не включать GitHub Actions; оставить Draft.
