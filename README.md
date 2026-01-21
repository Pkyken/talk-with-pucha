# talk-with-pucha

DBなし・最小JS・Docker Compose 1ファイルで起動できる軽量Web AIチャットです。C# (ASP.NET Core Minimal API) と Python (FastAPI) を使い、PINのみで認証します。

## 起動方法

```bash
docker compose up --build
```

起動後に http://localhost:8080/login を開きます。

## .env 例

```env
# 共通
TZ=UTC

# -----------------------------
# web (C# / ASP.NET Core)
# -----------------------------
APP_PIN=123456
APP_NAME=Talk with Pucha
FREE_DAILY_LIMIT=1000
COOLDOWN_SECONDS=2
MAX_INPUT_CHARS=4000
MAX_CONTEXT_MESSAGES=40
SESSION_TTL_HOURS=24
LLM_ADAPTER_URL=http://llm:8000/llm/generate
SYSTEM_PROMPT=あなたは{APP_NAME}のビジネス用アシスタントです。\n根拠と手順を明確に、推測は避け、敬語で簡潔に回答してください。\n今日は{DATE}（{TZ}）です。

# -----------------------------
# llm (Python / FastAPI)
# -----------------------------
OPENROUTER_API_KEY=PUT_YOUR_OPENROUTER_KEY_HERE
OPENROUTER_BASE_URL=https://openrouter.ai/api/v1
MODEL_CANDIDATES=model-a:free,model-b:free,model-c:free
REQUEST_TIMEOUT_SEC=30
MAX_OUTPUT_TOKENS=600
```

## 使い方

1. `/login` でPINを入力してログインします。
2. `/chat` に移動し、会話を行います。
3. Lock/Blur で画面のメッセージをぼかし、Panic Clear で画面から即時消去できます。

## 仕様メモ

- 日次制限はUTC日次 (`YYYY-MM-DD`) で `data/usage.json` に記録されます。
- 無料モデル候補 (`MODEL_CANDIDATES`) は運用者がOpenRouterのリストを見て更新してください。
- 会話履歴はサーバーメモリのみで、再起動すると消えます。
- `SYSTEM_PROMPT` を設定すると system メッセージがOpenRouter送信時に先頭へ追加されます。`{APP_NAME}` `{DATE}` `{TZ}` を含む場合はサーバー側で置換され、未設定または空の場合は system メッセージは追加されません。
