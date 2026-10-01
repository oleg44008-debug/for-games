#!/bin/bash
# Local ad-hoc signing of a COPY of a simple Godot .app on macOS.
# No game execution, certificate access, quarantine removal or notarization.
set -euo pipefail
export PATH=/usr/bin:/bin:/usr/sbin:/sbin

fail() { printf '%s\n' "Ошибка: $*" >&2; exit 1; }
usage() {
    printf '%s\n' 'Использование на Mac:' \
        '  /bin/bash macos-sign.command "/путь/PODIEZD.app"' \
        '  /bin/bash macos-sign.command "/путь/Игра.app" "/путь/Новая копия.app"' \
        '' \
        'Создаёт отдельную копию простой Godot .app и ставит локальную ad-hoc подпись.' \
        'Исходная .app и ZIP сохраняются. Существующие назначения не заменяются.' \
        'Это не Developer ID, не нотариализация и не подтверждение работоспособности.' \
        'Ограничения Gatekeeper и атрибут quarantine сохраняются.' \
        'Приложения с framework, дополнительным native кодом или ссылками требуют отдельного процесса подписи.'
}

if [ "${1:-}" = '--help' ] || [ "${1:-}" = '-h' ]; then usage; exit 0; fi
if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then usage; exit 64; fi
[ "$(/usr/bin/uname -s)" = 'Darwin' ] || fail 'Утилита работает только на macOS.'
[ -x /usr/bin/codesign ] || fail 'Не найден /usr/bin/codesign.'

input=${1%/}
case "$input" in *$'\n'*|*$'\r'*) fail 'Недопустимый путь приложения.' ;; esac
[ -d "$input" ] && [ ! -L "$input" ] || fail 'Нужна существующая .app, а не символическая ссылка.'
input_parent=$(cd -P -- "$(/usr/bin/dirname "$input")" && pwd)
app="$input_parent/$(/usr/bin/basename "$input")"
case "$app" in *.app) ;; *) fail 'Путь должен оканчиваться на .app.' ;; esac
[ -f "$app/Contents/Info.plist" ] || fail 'Нет Contents/Info.plist.'
/usr/bin/plutil -lint "$app/Contents/Info.plist" >/dev/null

# Refuse links instead of following nested signing destinations outside the copy.
links=$(/usr/bin/find "$app" -type l -print)
[ -z "$links" ] || fail 'Эта вспомогательная утилита не подписывает приложения с символическими ссылками.'
nested=$(/usr/bin/find "$app/Contents" -type d \( -name '*.app' -o -name '*.framework' -o -name '*.xpc' -o -name '*.appex' -o -name '*.bundle' \) -print)
[ -z "$nested" ] || fail 'Найден вложенный код. Нужна подпись компонентов изнутри наружу по правилам движка.'
libraries=$(/usr/bin/find "$app/Contents" -type f \( -name '*.dylib' -o -name '*.so' -o -name '*.so.*' \) -print)
[ -z "$libraries" ] || fail 'Дополнительные native библиотеки требуют отдельного процесса подписи.'

executable=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")
case "$executable" in ''|'.'|'..'|*/*|*\\*|*$'\n'*|*$'\r'*) fail 'Недопустимый CFBundleExecutable.' ;; esac
binary="$app/Contents/MacOS/$executable"
[ -f "$binary" ] && [ -x "$binary" ] || fail 'Отсутствует исполняемый файл приложения или Unix право запуска.'
case "$(/usr/bin/file -b "$binary")" in *Mach-O*) ;; *) fail 'Главный executable должен быть Mach-O.' ;; esac
[ -f "$app/Contents/Resources/$executable.pck" ] || fail 'Не найден Godot PCK с именем главного executable.'
for entry in "$app"/Contents/MacOS/* "$app"/Contents/MacOS/.[!.]* "$app"/Contents/MacOS/..?*; do
    [ -e "$entry" ] || continue
    [ "$entry" = "$binary" ] || fail 'Дополнительные executables требуют отдельного процесса подписи.'
done

output=${2:-"${app%.app}-local-signed.app"}
output=${output%/}
case "$output" in *$'\n'*|*$'\r'*) fail 'Недопустимый путь назначения.' ;; esac
output_parent=$(cd -P -- "$(/usr/bin/dirname "$output")" && pwd)
destination="$output_parent/$(/usr/bin/basename "$output")"
case "$destination" in *.app) ;; *) fail 'Назначение должно оканчиваться на .app.' ;; esac
[ ! -e "$destination" ] && [ ! -L "$destination" ] || fail 'Назначение уже существует. Выберите новый путь.'
case "$destination/" in "$app/"*) fail 'Копия не может находиться внутри исходного приложения.' ;; esac

# mkdir reserves a NEW destination atomically. Failed copies are kept for inspection.
/bin/mkdir "$destination"
printf 'Копирую в: %s\n' "$destination"
/usr/bin/ditto --rsrc --extattr "$app" "$destination"
# Existing Godot entitlements are retained. No blanket --deep --force signing.
/usr/bin/codesign --force --sign - --preserve-metadata=entitlements --timestamp=none "$destination"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$destination"
printf '\n%s\n' 'Локальная подпись проверена. Игра не запускалась; Gatekeeper и quarantine не изменялись.'
printf 'Копия: %s\n' "$destination"
