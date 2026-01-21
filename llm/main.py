import os
from typing import List, Optional

import httpx
from fastapi import FastAPI
from pydantic import BaseModel


class Message(BaseModel):
    role: str
    content: str


class LlmRequest(BaseModel):
    messages: List[Message]
    max_tokens: int


class LlmResponse(BaseModel):
    text: str
    model_used: str


class ErrorResponse(BaseModel):
    error_type: str


app = FastAPI()


def get_env(name: str, default: Optional[str] = None) -> str:
    value = os.getenv(name, default)
    if value is None or value.strip() == "":
        raise RuntimeError(f"Missing env: {name}")
    return value


OPENROUTER_API_KEY = get_env("OPENROUTER_API_KEY")
OPENROUTER_BASE_URL = get_env("OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1")
MODEL_CANDIDATES = [item.strip() for item in get_env("MODEL_CANDIDATES").split(",") if item.strip()]
REQUEST_TIMEOUT_SEC = float(get_env("REQUEST_TIMEOUT_SEC", "30"))
MAX_OUTPUT_TOKENS = int(get_env("MAX_OUTPUT_TOKENS", "600"))


@app.post("/llm/generate", response_model=None)
async def generate(request: LlmRequest):
    if not MODEL_CANDIDATES:
        return {"error_type": "failed"}

    headers = {
        "Authorization": f"Bearer {OPENROUTER_API_KEY}",
        "Content-Type": "application/json",
    }

    last_error = "failed"
    async with httpx.AsyncClient(timeout=REQUEST_TIMEOUT_SEC) as client:
        for model in MODEL_CANDIDATES:
            payload = {
                "model": model,
                "messages": [message.model_dump() for message in request.messages],
                "max_tokens": min(request.max_tokens, MAX_OUTPUT_TOKENS),
            }
            try:
                response = await client.post(f"{OPENROUTER_BASE_URL}/chat/completions", headers=headers, json=payload)
            except httpx.TimeoutException:
                last_error = "timeout"
                continue
            except httpx.RequestError:
                last_error = "upstream_error"
                continue

            if response.status_code == 200:
                data = response.json()
                text = data.get("choices", [{}])[0].get("message", {}).get("content", "")
                if text:
                    return {"text": text, "model_used": model}
                last_error = "failed"
                break

            if response.status_code == 429:
                last_error = "rate_limited"
                continue

            if 500 <= response.status_code < 600:
                last_error = "upstream_error"
                continue

            last_error = "failed"
            break

    return {"error_type": last_error}
