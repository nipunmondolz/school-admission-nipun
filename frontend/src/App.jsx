import React, { useCallback, useEffect, useState } from 'react';
import { api, serverNow } from './api.js';

const ID_RE = /^[0-9]{17}$/;
const POLL_MS = 3000; // class seats and my applications refresh this often

const STATUS_LABEL = {
  HELD: 'আসন ধরা আছে — ফি বাকি',
  CONFIRMED: 'ভর্তি নিশ্চিত',
  EXPIRED: 'মেয়াদ শেষ',
  CANCELLED: 'বাতিল',
};

function readStored() {
  try {
    return localStorage.getItem('studentId') || '';
  } catch {
    return '';
  }
}

function formatRemaining(ms) {
  const total = Math.max(0, Math.ceil(ms / 1000));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

export default function App() {
  const [studentInput, setStudentInput] = useState(readStored);
  const studentId = ID_RE.test(studentInput.trim()) ? studentInput.trim() : null;

  const [classes, setClasses] = useState(null);
  const [holds, setHolds] = useState(null);
  const [now, setNow] = useState(serverNow());
  const [notice, setNotice] = useState(null); // { type: 'ok' | 'error', text }
  const [connError, setConnError] = useState(null);
  const [busy, setBusy] = useState(false);
  const [newName, setNewName] = useState('');
  const [newSeats, setNewSeats] = useState('');

  // Remember the student id so a refresh / reopened browser still shows their applications.
  useEffect(() => {
    if (!studentId) return;
    try {
      localStorage.setItem('studentId', studentId);
    } catch {
      /* storage unavailable: the app still works, just without memory */
    }
  }, [studentId]);

  const loadClasses = useCallback(async () => {
    try {
      setClasses(await api('/classes'));
      setConnError(null);
    } catch (e) {
      setConnError(e.message);
    }
  }, []);

  const loadHolds = useCallback(async () => {
    if (!studentId) {
      setHolds([]);
      return;
    }
    try {
      setHolds(await api('/me/holds', { studentId }));
      setConnError(null);
    } catch (e) {
      setConnError(e.message);
    }
  }, [studentId]);

  useEffect(() => {
    loadClasses();
    const t = setInterval(loadClasses, POLL_MS);
    return () => clearInterval(t);
  }, [loadClasses]);

  useEffect(() => {
    setHolds(null);
    loadHolds();
    const t = setInterval(loadHolds, POLL_MS);
    return () => clearInterval(t);
  }, [loadHolds]);

  // One-second tick drives the countdowns and the automatic EXPIRED display.
  useEffect(() => {
    const t = setInterval(() => setNow(serverNow()), 1000);
    return () => clearInterval(t);
  }, []);

  const statusOf = (h) => (h.status === 'HELD' && Date.parse(h.expiresAt) <= now ? 'EXPIRED' : h.status);

  // When a hold lapses on screen, refresh right away so the freed seat shows up too.
  const lapsedKey = (holds || [])
    .filter((h) => h.status === 'HELD' && statusOf(h) === 'EXPIRED')
    .map((h) => h.holdId)
    .join(',');
  useEffect(() => {
    if (lapsedKey) {
      loadClasses();
      loadHolds();
    }
  }, [lapsedKey, loadClasses, loadHolds]);

  async function run(action, okText) {
    setNotice(null);
    setBusy(true);
    try {
      await action();
      if (okText) setNotice({ type: 'ok', text: okText });
    } catch (e) {
      setNotice({ type: 'error', text: e.message });
    } finally {
      setBusy(false);
      await Promise.all([loadClasses(), loadHolds()]);
    }
  }

  function needStudent() {
    setNotice({ type: 'error', text: 'প্রথমে উপরের ঘরে শিক্ষার্থীর ১৭ অঙ্কের জন্মনিবন্ধন নম্বর লিখুন।' });
  }

  function createClass(e) {
    e.preventDefault();
    const name = newName.trim();
    const seats = Number(newSeats);
    if (!name) return setNotice({ type: 'error', text: 'ক্লাসের নাম লিখুন।' });
    if (!Number.isInteger(seats) || seats < 1)
      return setNotice({ type: 'error', text: 'আসন-সংখ্যা ১ বা তার বেশি পূর্ণসংখ্যা হতে হবে।' });
    run(async () => {
      await api('/classes', { method: 'POST', body: { name, totalSeats: seats } });
      setNewName('');
      setNewSeats('');
    }, `ক্লাস "${name}" তৈরি হয়েছে।`);
  }

  function apply(c) {
    if (!studentId) return needStudent();
    run(() => api(`/classes/${c.id}/holds`, { method: 'POST', studentId }),
      `"${c.name}"-এ আসন ধরা হয়েছে। মেয়াদের মধ্যে ভর্তি ফি দিন।`);
  }

  function pay(h) {
    run(() => api(`/holds/${h.holdId}/confirm`, { method: 'POST', studentId }),
      'ভর্তি ফি জমা হয়েছে, ভর্তি নিশ্চিত।');
  }

  function cancel(h) {
    run(() => api(`/holds/${h.holdId}`, { method: 'DELETE', studentId }), 'আবেদন বাতিল হয়েছে।');
  }

  const myList = holds || [];
  const activeClassIds = new Set(
    myList.filter((h) => ['HELD', 'CONFIRMED'].includes(statusOf(h))).map((h) => h.classId),
  );
  const statusCards = myList.filter((h) => ['HELD', 'EXPIRED'].includes(statusOf(h))).slice(0, 5);

  return (
    <main>
      <h1>স্কুল ভর্তি</h1>

      <section className="card">
        <label htmlFor="sid">শিক্ষার্থীর জন্মনিবন্ধন নম্বর (১৭ অঙ্ক)</label>
        <input
          id="sid"
          inputMode="numeric"
          maxLength={17}
          placeholder="যেমন: 20152691234567890"
          value={studentInput}
          onChange={(e) => setStudentInput(e.target.value.replace(/[^0-9]/g, ''))}
        />
        {studentInput && !studentId && (
          <p className="hint error-text">{studentInput.length}/১৭ অঙ্ক — ঠিক ১৭টি অঙ্ক লিখতে হবে।</p>
        )}
        {studentId && <p className="hint">এই নম্বর দিয়ে আপনার আবেদনগুলো দেখানো হচ্ছে।</p>}
      </section>

      {connError && <div className="banner error" role="alert">{connError}</div>}
      {notice && (
        <div className={`banner ${notice.type}`} role="alert">
          {notice.text}
        </div>
      )}

      <section className="card">
        <h2>ক্লাস তৈরি (স্কুল অ্যাডমিন)</h2>
        <form onSubmit={createClass} className="row">
          <input
            aria-label="ক্লাসের নাম"
            placeholder="ক্লাসের নাম, যেমন: Class 6 - Morning Shift"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
          />
          <input
            aria-label="আসন-সংখ্যা"
            type="number"
            min="1"
            placeholder="আসন-সংখ্যা"
            value={newSeats}
            onChange={(e) => setNewSeats(e.target.value)}
          />
          <button type="submit" disabled={busy}>তৈরি করুন</button>
        </form>
      </section>

      <section className="card">
        <h2>ক্লাস তালিকা</h2>
        {classes === null ? (
          <p>লোড হচ্ছে…</p>
        ) : classes.length === 0 ? (
          <p>এখনও কোনো ক্লাস তৈরি হয়নি।</p>
        ) : (
          <div className="scroll">
            <table>
              <thead>
                <tr>
                  <th>ক্লাস</th><th>মোট আসন</th><th>হোল্ডে</th><th>নিশ্চিত</th><th>খালি</th><th></th>
                </tr>
              </thead>
              <tbody>
                {classes.map((c) => (
                  <tr key={c.id}>
                    <td>{c.name}</td>
                    <td>{c.totalSeats}</td>
                    <td>{c.heldSeats}</td>
                    <td>{c.confirmedSeats}</td>
                    <td><strong>{c.availableSeats}</strong></td>
                    <td>
                      {activeClassIds.has(c.id) ? (
                        <span className="muted">আবেদন করা আছে</span>
                      ) : (
                        <button onClick={() => apply(c)} disabled={busy || c.availableSeats === 0}>
                          {c.availableSeats === 0 ? 'আসন শেষ' : 'আবেদন করুন'}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="card">
        <h2>আবেদনের অবস্থা</h2>
        {!studentId ? (
          <p className="muted">জন্মনিবন্ধন নম্বর লিখলে এখানে আপনার চলমান আবেদন দেখা যাবে।</p>
        ) : holds === null ? (
          <p>লোড হচ্ছে…</p>
        ) : statusCards.length === 0 ? (
          <p className="muted">এই মুহূর্তে কোনো চলমান আবেদন নেই।</p>
        ) : (
          statusCards.map((h) => {
            const st = statusOf(h);
            const active = st === 'HELD';
            return (
              <div key={h.holdId} className={`hold ${st.toLowerCase()}`}>
                <div>
                  <strong>{h.className}</strong>
                  <div className="status">{STATUS_LABEL[st]}</div>
                  {active && (
                    <div className="timer">মেয়াদ শেষ হতে বাকি: {formatRemaining(Date.parse(h.expiresAt) - now)}</div>
                  )}
                </div>
                <div className="row">
                  <button onClick={() => pay(h)} disabled={!active || busy}>ভর্তি ফি দিন</button>
                  <button className="secondary" onClick={() => cancel(h)} disabled={!active || busy}>বাতিল</button>
                </div>
              </div>
            );
          })
        )}
      </section>

      <section className="card">
        <h2>আমার আবেদন</h2>
        {!studentId ? (
          <p className="muted">জন্মনিবন্ধন নম্বর লিখলে এখানে আপনার সব আবেদন দেখা যাবে।</p>
        ) : holds === null ? (
          <p>লোড হচ্ছে…</p>
        ) : myList.length === 0 ? (
          <p className="muted">এই শিক্ষার্থীর কোনো আবেদন নেই।</p>
        ) : (
          <div className="scroll">
            <table>
              <thead>
                <tr><th>ক্লাস</th><th>অবস্থা</th></tr>
              </thead>
              <tbody>
                {myList.map((h) => (
                  <tr key={h.holdId}>
                    <td>{h.className}</td>
                    <td><span className={`badge ${statusOf(h).toLowerCase()}`}>{STATUS_LABEL[statusOf(h)]}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </main>
  );
}
