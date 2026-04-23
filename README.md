# AnthropicProxy 1.0
![](Images/AnthropicProxy.jpg)

Небольшой локальный прокси на C# / ASP.NET Core, который позволяет запустить `Claude Desktop` в `gateway`-режиме и перенаправить запросы в OpenRouter. Без использования данного прокси при авторизации на сайте Anthropic (claude.ai) при использовании бесплатного тарифного плана (и, возможно, даже при использовании плана Pro) в десктопном приложении вместо вкладок Cowork и Code будет лишь вкладка Chat с довольно ограниченным применением. Данная штука позволит прикоснуться к довольно хайповой штуковине Cloude и "потрогать" её неочевидные фичи до довольно внушительных затрат. 

## Скриншоты
![](Images/ClaudeGatemodeAuth.jpg)
![](Images/CloudeCoworkProject.jpg)  
![](Images/CloudeCodeModelMapping.jpg)
![](Images/ClaudeCodeDesktop.jpg)

## Особенности этой наскоро собранной мини-софтинки
 
Текущий MVP специально сделан простым:

- отвечает на `GET /health`
- отвечает на `GET /v1/models`
- принимает `POST /v1/messages`
- принудительно маппит все модели в `openrouter/free` (на данный момент обращение по API к этой "мультимодельке" совершенно бесплатно и с вполне терпимыми лимитами по числу запросов в минуту/час/день)
- работает на Windows без Docker
- С# - исходники собираются через обычный .NET SDK (ASP.NET) и без Rust 

Важно: это экспериментальный проект, а не официальный продукт Anthropic или OpenRouter.

## Что уже умеет

- `Claude Desktop` видит локальный gateway и позволяет через этот gateway успешно авторизоваться
- запросы реально уходят в OpenRouter

## Что пока не реализовано

- `stream=true`
- тонкая совместимость со всеми вариантами `Claude Desktop`
- полноценная поддержка режима крутых моделей для `Code` (сейчас, похоже, маппинг срабатывает лишь для "фирменной" модели Haiku)
- продвинутое логирование, retries и model capabilities

## Что потребуется

- Windows 10/11 x64
- .NET 8 SDK или новее
- установленный `Claude Desktop`
- OpenRouter API key

## Как запустить

### Вариант 1. Через `dotnet run`

1. Откройте PowerShell в папке проекта.
2. Задайте свой OpenRouter key:

```powershell
$env:OPENROUTER_API_KEY="ВАШ_OPENROUTER_API_KEY"
```

3. Запустите проект:

```powershell
dotnet run --launch-profile http
```

После запуска прокси будет слушать:

```text
http://127.0.0.1:3000
```

### Вариант 2. Через готовый exe

1. Сначала соберите проект:

```powershell
dotnet build
```

2. Затем запустите:

```powershell
$env:OPENROUTER_API_KEY="ВАШ_OPENROUTER_API_KEY"
.\bin\Debug\net8.0\AnthropicProxy.exe
```

## Быстрая проверка, что прокси жив

Откройте второе окно PowerShell и выполните:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:3000/health" -Method Get
```

Ожидаемый ответ:

```json
{
  "status": "ok"
}
```

Проверка списка моделей:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:3000/v1/models" -Method Get
```

## Как включить `Claude Desktop` в gateway-режим

В репозитории есть два готовых файла:

- [desktop-gateway-enable.reg](./desktop-gateway-enable.reg)
- [desktop-gateway-disable.reg](./desktop-gateway-disable.reg)

### Перед включением сделайте резервную копию реестра через выполнение в PowerShell команды:

```powershell
reg export HKCU\SOFTWARE\Policies\Claude .\claude-policy-backup.reg /y
```

Если ветка ещё не существует, Windows может выдать ошибку. Это нормально.

### Включение

1. Полностью закройте `Claude Desktop`.
2. Убедитесь, что `AnthropicProxy` уже запущен.
3. Импортируйте настройки:

```powershell
reg import .\desktop-gateway-enable.reg
```

4. Снова запустите `Claude Desktop`.

После этого приложение должно увидеть локальный gateway и предложить вход через него.

## Как вернуть всё обратно

1. Закройте `Claude Desktop`.
2. Импортируйте файл отката:

```powershell
reg import .\desktop-gateway-disable.reg
```

3. Если хотите восстановить именно прежнее состояние ветки, а не просто удалить настройки gateway:

```powershell
reg import .\claude-policy-backup.reg
```

4. Снова запустите `Claude Desktop`.

## Как этим пользоваться каждый день

Самая простая схема такая:

1. Запустите `AnthropicProxy`
2. Дождитесь строки вроде:

```text
Now listening on: http://127.0.0.1:3000
```

3. Запустите `Claude Desktop`
4. Используйте `Cowork` / `Code` (если они доступны на вашем тариф. планеи вообще в вашей версии Claude Desktop)

Если прокси не запущен, `Claude Desktop` в gateway-режиме не сможет нормально работать.

## Почему в списке моделей могут быть странные подписи

Прокси сейчас отдаёт Anthropic-подобный список моделей, но по факту всё равно перенаправляет запросы в `openrouter/free`.

Поэтому в интерфейсе можно увидеть, например:

- `Haiku`
- `Sonnet`
- `Opus`
- `OpenRouter Free`

Но реально все эти варианты сейчас маппятся в один и тот же upstream:

```text
openrouter/free
```

## Ограничения и известные нюансы

- `Cowork` может заработать, а вот `Code` нет (причины этого предстоит еще выяснить, так как `Code` в `Claude Desktop` может зависеть от дополнительных проверок, которые этот MVP пока не покрывает)
- некоторые free-модели OpenRouter могут возвращать ответы с reasoning-блоками или нестандартным содержимым
- для старых или облегчённых Windows-сборок может понадобиться включение `VirtualMachinePlatform`

## Безопасность

Если форкнули мой репозиторий и модифицировали его, то не публикуйте свой реальный OpenRouter API key в GitHub. Просто перед коммитом проверьте
 `Properties/launchSettings.json`.

В шаблоне этого проекта ключ в `launchSettings.json` уже заменён на безопасную заглушку.

## Лицензия и статус

Используется стандартная лицензия MIT.
Проект находится в состоянии MVP / R&D.
Используйте на свой страх и риск.

## .
Как есть. "Сделай сам", что называется. Код разработан с применением агентной IDE Codex и модели GPT-5.4 

## ..
Исследователь медиа, 2026
