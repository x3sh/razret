# Проверка двух независимых обновлений

Тесты используют отдельную папку с фиктивными настройками и пользовательским списком. Движок не запускается, DNS и сетевые настройки не меняются. Проект имеет версию 0.7.0 только для возможности скачать текущий публичный выпуск приложения через тот же код, что используется в Razret; публиковать тестовый релиз не нужно.

```powershell
dotnet build tests/UpdateIntegration/UpdateIntegration.csproj -c Release -o build/update-integration-check
dotnet build/update-integration-check/UpdateIntegration.dll C:/Zapret/research/qa-updates --live
```

`--live` проверяет настоящий GitHub API, скачивание приложения, SHA256 и версию EXE, замену и резервную копию, независимую установку zapret, перенос списков, откат и отмену загрузки приложения.

Для проверки процесса установки сначала соберите автономный Razret через `release.ps1`. Все обычные копии Razret должны быть закрыты: помощник использует штатный mutex приложения. Фиктивная следующая версия запускается только для записи маркера успешного перезапуска.

```powershell
& C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe /nologo /target:winexe /out:build/update-integration-check/RestartFixture.exe tests/UpdateIntegration/RestartFixture.cs.txt
dotnet build/update-integration-check/UpdateIntegration.dll C:/Zapret/research/qa-updates --helper C:/Zapret/build/release-0.7.8/Razret.exe C:/Zapret/build/update-integration-check/RestartFixture.exe
```

Этот тест запускает реальный автономный помощник обновления, пока тестовый старый процесс держит файл EXE. Проверяется ожидание освобождения файла, атомарная замена, сохранение предыдущего EXE, перезапуск и неизменность настроек и движка. Компилятор .NET Framework используется только для тестового маркера; пользователям Razret его устанавливать не требуется.
