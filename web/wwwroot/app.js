const messagesEl = document.getElementById('messages');
const inputEl = document.getElementById('input');
const sendBtn = document.getElementById('send');
const statusEl = document.getElementById('status');
const lockToggle = document.getElementById('lockToggle');
const panicClear = document.getElementById('panicClear');
const lockSession = document.getElementById('lockSession');
let composing = false;
let suppressEnterOnce = false;

const errorMessages = {
  unauthorized: '認証が必要です。',
  empty: '入力が空です。',
  too_long: '入力が長すぎます。',
  cooldown: 'クールダウン中です。',
  daily_limit: '本日の上限に達しました。',
  llm_unreachable: 'LLMに接続できません。',
  llm_error: 'LLMでエラーが発生しました。',
  llm_invalid: 'LLMの応答が不正です。',
  llm_failed: 'LLMで失敗しました。',
  rate_limited: 'レート制限です。',
  timeout: 'タイムアウトです。',
  upstream_error: '上流エラーです。',
  failed: '失敗しました。'
};

const appendMessage = (role, text) => {
  const wrapper = document.createElement('div');
  wrapper.className = `message ${role}`;
  wrapper.textContent = text;
  messagesEl.appendChild(wrapper);
  messagesEl.scrollTop = messagesEl.scrollHeight;
};

const setStatus = (text) => {
  statusEl.textContent = text;
};

const resolveErrorMessage = (codeOrMessage, status) => {
  if (!codeOrMessage && status) {
    return `エラー: ${status}`;
  }
  return errorMessages[codeOrMessage] || codeOrMessage || '不明なエラーです。';
};

const sendMessage = async () => {
  const text = inputEl.value.trim();
  if (!text) {
    return;
  }

  appendMessage('user', text);
  inputEl.value = '';
  setStatus('送信中...');

  try {
    const response = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ input: text })
    });

    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      appendMessage('system', resolveErrorMessage(error.error, response.status));
      setStatus('');
      return;
    }

    const data = await response.json();
    appendMessage('assistant', data.text || '');
    if (data.model_used) {
      setStatus(`使用モデル: ${data.model_used}`);
    } else {
      setStatus('');
    }
  } catch (err) {
    appendMessage('system', 'ネットワークエラーです。');
    setStatus('');
  }
};

sendBtn.addEventListener('click', sendMessage);
inputEl.addEventListener('compositionstart', () => {
  composing = true;
  suppressEnterOnce = true;
});
inputEl.addEventListener('compositionupdate', () => {
  suppressEnterOnce = true;
});
inputEl.addEventListener('compositionend', () => {
  composing = false;
  suppressEnterOnce = true;
});
inputEl.addEventListener('beforeinput', (event) => {
  if (event && event.isComposing) {
    suppressEnterOnce = true;
  }
});
inputEl.addEventListener(
  'keydown',
  (event) => {
    if (event.key !== 'Enter' || event.shiftKey) {
      return;
    }

    const imeHint = composing || event.isComposing || event.keyCode === 229;
    if (imeHint || suppressEnterOnce) {
      suppressEnterOnce = false;
      event.preventDefault();
      event.stopImmediatePropagation();
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    sendMessage();
  },
  true
);

lockToggle.addEventListener('click', () => {
  messagesEl.classList.toggle('blurred');
});

panicClear.addEventListener('click', () => {
  messagesEl.innerHTML = '';
  setStatus('画面をクリアしました。');
});

lockSession.addEventListener('click', async () => {
  await fetch('/api/lock', { method: 'POST' });
  window.location.href = '/login';
});
