# Поновлення доступу до Google Drive

Цей деплой використовує OAuth-токени rclone, а не Google Drive API key. Якщо доступ зникає через 7 днів, найімовірніша причина — режим **Testing** Google OAuth-застосунку. Access token короткочасний і поновлюється автоматично; прострочений або відкликаний refresh token потребує повторної авторизації. `invalid_grant` / `Token has been expired or revoked` у логах вказує на проблему refresh token, але саме по собі не доводить причину.

## 1. Усунути причину щотижневого протухання

У [Google Cloud Console](https://console.cloud.google.com/) вибери проєкт, якому належить `client_id` твого remote `gdrive`. Відкрий **Google Auth Platform → Audience** (у старому інтерфейсі **OAuth consent screen**) і для External-застосунку натисни **Publish app**, щоб Publishing status став **In production**. Після цього обов'язково пройди авторизацію заново одним зі способів нижче: старий токен, виданий у Testing, не варто використовувати далі. Production прибирає семиденне обмеження Testing, але токен усе одно може бути відкликаний.

## 2. Локально, потім передати конфіг у Coolify

Виконуй у приватній папці з наявним `rclone.conf`, де вже налаштований remote `gdrive`:

```sh
rclone --config ./rclone.conf config reconnect gdrive:
rclone --config ./rclone.conf lsf gdrive:Media --max-depth 1
base64 < ./rclone.conf | tr -d '\n'
```

Під час reconnect дозволь відкриття браузера й увійди в Google-акаунт із медіа. У перевірці заміни `gdrive:Media` на фактичне значення `GDRIVE_REMOTE`, якщо воно інше. Остання команда виводить секрет: внеси весь результат у **runtime secret** `RCLONE_CONFIG_BASE64` у Coolify, збережи та виконай **Redeploy**, щоб контейнер отримав нове значення. Простого `docker restart` недостатньо для зміни environment.

Якщо локального конфіга немає, створи його через `rclone --config ./rclone.conf config`: remote `gdrive`, тип `drive`, власний OAuth client типу **Desktop app**, scope `drive.readonly`. Збережи ті самі налаштування папки/Shared Drive, що були на сервері. Деталі: [початкове налаштування](README.md#google-drive-authorization-one-time). Потім виконай перевірку та кодування вище.

## 3. Безпосередньо на сервері, навіть якщо контейнер падає

Підключись до Docker-сервера через SSH. Знайди саме контейнер Jellyfin цього застосунку; підстав його ім'я або ID замість `REPLACE_ME`. Команди зупинять Jellyfin на час авторизації та змінять конфіг у його наявному volume:

```sh
docker ps -a --format 'table {{.ID}}\t{{.Names}}\t{{.Status}}'
JELLYFIN_CONTAINER='REPLACE_ME'
JELLYFIN_IMAGE=$(docker inspect --format '{{.Image}}' "$JELLYFIN_CONTAINER")
docker stop "$JELLYFIN_CONTAINER"
docker run --rm -it --volumes-from "$JELLYFIN_CONTAINER" \
  --entrypoint rclone "$JELLYFIN_IMAGE" \
  --config /rclone-config/rclone.conf config reconnect gdrive:
```

На запит про браузер на сервері відповідай **No**. rclone покаже команду `rclone authorize ...`: виконай її **на локальному комп'ютері з браузером**, увійди в Google і встав отриманий результат у серверний запит `config_token>`. Використовуй саме згенеровану команду, щоб зберегти OAuth client і scope. Бажано мати ту саму версію rclone локально й на сервері; у цьому Dockerfile закріплена **1.70.3**.

Після успішного reconnect перевір доступ і запусти Jellyfin:

```sh
docker run --rm --volumes-from "$JELLYFIN_CONTAINER" \
  --entrypoint rclone "$JELLYFIN_IMAGE" \
  --config /rclone-config/rclone.conf lsf gdrive:Media --max-depth 1
docker start "$JELLYFIN_CONTAINER"
docker logs --tail 100 "$JELLYFIN_CONTAINER"
```

Для перевірки також підстав фактичний `GDRIVE_REMOTE`. Якщо авторизація або перевірка не вдалася, виправ помилку перед запуском; для повернення контейнера до запуску є `docker start` вище. Перевір відтворення медіа в Jellyfin.

## Збереження токенів у цьому деплої

Актуальний конфіг лежить у `/rclone-config/rclone.conf` у постійному volume `rclone-config`. [entrypoint.sh](entrypoint.sh) зберігає автоматично поновлені токени, поки `RCLONE_CONFIG_BASE64` не змінюється. Нове значення секрету перезаписує конфіг; те саме значення з наявним `bootstrap.sha256` його зберігає. Після серверного reconnect можна прибрати старий bootstrap-секрет із Coolify та зробити Redeploy, зберігши volume; для відновлення на новому сервері потрібна захищена резервна копія актуального конфіга.

Base64 не є шифруванням. Не коміть конфіг/токени, не надсилай їх у чат і не видаляй volumes (`docker compose down -v`). Видалення `rclone-config` знищить поновлений конфіг, а старий bootstrap-секрет може повернути прострочений токен.

Джерела: [rclone reconnect](https://rclone.org/commands/rclone_config_reconnect/), [авторизація без браузера на сервері](https://rclone.org/remote_setup/), [Google: строк дії refresh token](https://developers.google.com/identity/protocols/oauth2#expiration).
