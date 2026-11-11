// Backend URL: runtime config (Docker) > Vite env at build time > localhost default.
const baseUrl = () =>
  (window.__APP_CONFIG__?.API_URL || import.meta.env.VITE_API_URL || 'http://localhost:8080').replace(/\/+$/, '');

// Difference between the server clock and this browser's clock, learned from the HTTP Date header.
// Used so the countdown matches the server's idea of "expired" even if the PC clock is off.
let serverOffsetMs = 0;
export const serverNow = () => Date.now() + serverOffsetMs;

export class ApiError extends Error {
  constructor(message, status, code) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

export async function api(path, { method = 'GET', studentId, body } = {}) {
  const headers = {};
  if (studentId) headers['X-Student-Id'] = studentId;
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  let res;
  try {
    res = await fetch(baseUrl() + path, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw new ApiError('সার্ভারের সাথে যোগাযোগ করা যাচ্ছে না। ইন্টারনেট বা সার্ভার চালু আছে কি না দেখুন, তারপর আবার চেষ্টা করুন।', 0, 'NETWORK');
  }

  const dateHeader = res.headers.get('Date');
  if (dateHeader) {
    const t = Date.parse(dateHeader);
    if (!Number.isNaN(t)) serverOffsetMs = t - Date.now();
  }

  if (res.status === 204) return null;

  let data = null;
  try {
    data = await res.json();
  } catch {
    /* body is not JSON */
  }

  if (!res.ok) {
    throw new ApiError(
      data?.message || `অনুরোধ সফল হয়নি (কোড ${res.status})। একটু পরে আবার চেষ্টা করুন।`,
      res.status,
      data?.code,
    );
  }
  return data;
}
