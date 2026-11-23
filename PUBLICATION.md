# Публикация на GitHub

Документ с готовыми текстами и чек-листом для выгрузки ZAPRET//CTRL 1.1.

## Рекомендуемые настройки репозитория

- **Название:** `zapret-control`
- **Видимость:** Public
- **Описание:** `Cyberpunk GUI for managing zapret DPI bypass on Windows 10`
- **Topics:** `zapret`, `dpi`, `dpi-bypass`, `windows`, `windows-10`, `wpf`, `csharp`, `dotnet`, `cyberpunk`, `gui`
- **Основная ветка:** `main`
- **Релиз:** `v1.1.0`

## Короткое описание для GitHub About

```text
Cyberpunk GUI for managing zapret DPI bypass on Windows 10. Tray, diagnostics, speed tests, safe process control.
```

## Описание релиза v1.1.0

```markdown
# ZAPRET//CTRL 1.1.0

Расширенное обновление графической панели для zapret на Windows 10.

## Что внутри
- Запуск и остановка обхода через winws.exe.
- Автоматическое чтение профилей .bat.
- Определение внешнего winws.exe по заголовку консоли.
- Безопасный режим: управление заблокировано до явной разблокировки.
- Трей, диагностика, замер скорости Cloudflare и YouTube CDN, график истории.
- Редактирование пользовательских списков zapret с .bak.
- Настройки пути, прав, автозапуска, трея и оформления.
- Новое в 1.1: бэкапы и восстановление, автоблокировка управления, проверка сайта, мини-монитор, ночные замеры, избранные стратегии, журнал с фильтрами, история статусов, детали процесса, проверка целостности профилей, уведомления, портативный режим.

## Требования
- Windows 10 x64.
- .NET 8 Desktop Runtime.
- Установленный zapret с bin\winws.exe.
- Права администратора для запуска и остановки обхода.

## Файлы
- `ZapretControl.exe` — framework-dependent single-file сборка.
- `README.txt` — краткая инструкция.
- `SHA256.txt` — контрольная сумма.

## Важно
Приложение не заменяет VPN, не устанавливает службы и драйверы, не меняет системные настройки. Выход из приложения не останавливает обход.
```

## Что положить в репозиторий

```text
README.md
CHANGELOG.md
PUBLICATION.md
LICENSE
.gitignore
ZapretControl.csproj
app.manifest
app.ico
App.xaml
App.xaml.cs
GlobalUsings.cs
MainWindow.xaml
MainWindow.xaml.cs
MonitorWindow.xaml
MonitorWindow.xaml.cs
Converters/
Core/
Themes/
tools/
docs/screenshots/
dist/
  ZapretControl.exe
  README.txt
  SHA256.txt
```

Не выкладывать:

- `bin/`
- `obj/`
- `.vs/`
- пользовательские конфиги и логи из `%LOCALAPPDATA%\ZapretControl`
- папку zapret с чужими бинарниками и драйверами
- личные пути и скриншоты с локальными данными

## Чек-лист перед публикацией

- [ ] Версия в `ZapretControl.csproj`, интерфейсе и README совпадает: `1.1.0`.
- [ ] В README нет личных путей и названий локальных папок.
- [ ] Exe собран в Release и проверен запуском.
- [ ] `SHA256.txt` соответствует опубликованному exe.
- [ ] В репозитории нет `bin/`, `obj/`, `publish/` и пользовательских данных.
- [ ] Добавлен `.gitignore`.
- [ ] Проверены скриншоты: без личных доменов, путей и PID, если они не нужны.
- [ ] Выбранная лицензия добавлена отдельным файлом `LICENSE`.
- [ ] Создан тег `v1.1.0` и релиз с прикреплённым exe.
- [ ] В описании релиза есть предупреждение, что это неофициальный GUI и не VPN.

## Публикация через сайт GitHub

1. Создай новый публичный репозиторий `zapret-control`.
2. Не добавляй README и `.gitignore` на сайте — они уже есть в папке.
3. Загрузи содержимое папки `ZapretControl-1.1-GitHub` в корень репозитория.
4. Проверь, что `README.md` отображается на главной странице.
5. Создай релиз `v1.1.0`, прикрепи `ZapretControl.exe` и `SHA256.txt`.
6. Вставь описание релиза из блока выше.

## Публикация через Git

```powershell
cd ZapretControl-1.1-GitHub
git init
git add .
git commit -m "ZAPRET//CTRL 1.1.0"
git branch -M main
git remote add origin https://github.com/USERNAME/zapret-control.git
git push -u origin main
```

Тег и релиз:

```powershell
git tag -a v1.1.0 -m "ZAPRET//CTRL 1.1.0"
git push origin v1.1.0
```

## Лицензия

Выбрана лицензия **MIT**. Файл `LICENSE` лежит в корне репозитория.

Copyright (c) 2026 Zapret Control. Если публикуешь под своим именем или ником, замени строку copyright в `LICENSE` перед пушем.

## Отказ от ответственности

ZAPRET//CTRL — неофициальный GUI для zapret. Проект не связан с авторами zapret, не является VPN и не гарантирует обход конкретных блокировок. Использование — на свой риск и в соответствии с местным законодательством.
