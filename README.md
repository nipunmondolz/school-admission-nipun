# স্কুল ভর্তি সিস্টেম (School Admission System)

"আগে আবেদন করলে আগে পাবে" ভিত্তিতে ভর্তি: আবেদন করলে সিট নির্দিষ্ট সময়ের জন্য **hold** থাকে, সময়ের মধ্যে ফি দিলে **confirm** হয়, না দিলে সিট নিজে থেকেই ছাড়া পায়।

Stack: .NET 8 (ASP.NET Core minimal API, EF Core) · PostgreSQL 16 · React 18 (Vite) · Docker

## চালানোর ধাপ

শুধু Docker লাগবে (.NET, Node বা PostgreSQL আলাদা install করতে হবে না):

```bash
docker compose up
```

প্রথমবার image build হতে কিছু সময় লাগে। চালু হলে:

| অংশ | URL |
|---|---|
| **Frontend (UI)** | http://localhost:3000 |
| Backend API | http://localhost:8080 |

থামাতে `Ctrl+C`, তারপর `docker compose down`। `down` করে আবার `up` দিলেও data থাকে (PostgreSQL volume `pgdata`)। data সহ সব মুছতে: `docker compose down -v`।

### ব্যবহার

1. "ক্লাস তৈরি" অংশে নাম ও আসন-সংখ্যা দিয়ে ক্লাস বানান (স্কুল অ্যাডমিন)।
2. উপরের ঘরে ১৭ অঙ্কের জন্মনিবন্ধন নম্বর লিখুন।
3. ক্লাস তালিকা থেকে **আবেদন করুন** চাপুন। "আবেদনের অবস্থা" অংশে countdown দেখা যাবে।
4. মেয়াদের মধ্যে **ভর্তি ফি দিন** চাপলে ভর্তি নিশ্চিত হয়; **বাতিল** চাপলে সিট ছাড়া পায়।
5. মেয়াদ শেষ হলে আবেদন নিজে থেকেই "মেয়াদ শেষ" দেখায় ও ফি-বাটন বন্ধ হয়ে যায়।

### কনফিগারেশন (environment variable)

| Variable | কোথায় | মানে | default |
|---|---|---|---|
| `HOLD_DURATION_SECONDS` | backend | hold-এর মেয়াদ (সেকেন্ড) | `600` |
| `DB_CONNECTION` | backend | Npgsql connection string | `docker-compose.yml`-এ দেওয়া |
| `API_URL` | frontend | **ব্রাউজার** যে URL-এ backend পায় | `http://localhost:8080` |

উদাহরণ (মেয়াদ ৩০ সেকেন্ড করে চালানো, expiry দেখার জন্য সুবিধাজনক):

```bash
HOLD_DURATION_SECONDS=30 docker compose up
```

## Test চালানো

Backend-এর automated test (PostgreSQL-এ চলে; আলাদা database `admission_test` ব্যবহার করে, তাই আপনার data ছোঁয় না):

```bash
docker compose run --rm tests
```

Test-গুলো:

- ২০০টি আবেদন একসঙ্গে, ১০টি সিট → ঠিক ১০টি সফল (`201`), বাকি ১৯০টি `409`; কোনো `5xx` নয়
- **দুটি আলাদা app instance, একই database**, ১০০টি আবেদন একসঙ্গে, ৫টি সিট → ঠিক ৫টি সফল
- একই শিক্ষার্থী একই ক্লাসে দুইবার নয় (২০টি সমান্তরাল চেষ্টায়ও একটিই সফল); অন্য ক্লাসে চলে
- confirm বারবার (১০টি সমান্তরাল) → `confirmedSeats` একবারই বাড়ে; অন্যের আবেদন confirm করলে `403`
- মেয়াদ শেষ → কোনো API call ছাড়াই সিট ফেরত, confirm করলে `409`, আবার আবেদন করা যায়
- বাতিল → সিট ফেরত; ভুল input ও অজানা id → সবসময় `4xx`, কখনো `500` নয়

Frontend-এর জন্য আলাদা automated test নেই (brief-এ পরিমাণ/scope স্পষ্ট ছিল না)।

## Bonus: দুটি backend process, একই database

দ্বিতীয় backend চালাতে (port `8081`):

```bash
docker compose --profile bonus up
```

### `held + confirmed ≤ totalSeats` কীভাবে নিশ্চিত হয়

নিয়মটি **application-এর memory-তে নয়, PostgreSQL-এ** রক্ষা করা হয়, তাই কতগুলো process চলল তাতে কিছু আসে-যায় না:

1. সিটের সংখ্যা বদলাতে পারে এমন প্রতিটি কাজ (আবেদন, confirm, cancel) একটি **transaction**-এর ভেতরে প্রথমে ওই ক্লাসের সারিতে `SELECT … FOR UPDATE` দিয়ে row lock নেয়। একই ক্লাসের দুটি কাজ কখনো একসঙ্গে সিদ্ধান্ত নিতে পারে না; দ্বিতীয়টি অপেক্ষা করে এবং প্রথমটি commit করার পর **হালনাগাদ অবস্থা দেখে** সিদ্ধান্ত নেয়।
2. Lock-এর ভেতরে গোনা হয়: `CONFIRMED` + (`HELD` যার `ExpiresAt > এখন`)। সংখ্যা `totalSeats`-এর সমান বা বেশি হলে `409`।
3. ভিন্ন ক্লাসের কাজ একে অপরকে আটকায় না, কারণ lock প্রতি-ক্লাস।
4. দ্বিতীয় সুরক্ষা: database-এ `UNIQUE (ClassId, StudentId) WHERE Status IN ('HELD','CONFIRMED')` এবং `CHECK (TotalSeats > 0)`। কোড ভুল করলেও একই শিক্ষার্থীর দ্বিতীয় সক্রিয় আবেদন database-ই ফেরত দেয়।

Test `Two_app_instances_on_one_database_never_exceed_total_seats` দুটি আলাদা app instance একই database-এ চালিয়ে এটি পরীক্ষা করে।

## মেয়াদ (expiry) কীভাবে কাজ করে

মেয়াদ শেষ হওয়ার জন্য কোনো background job বা API call-এর উপর নির্ভর করা হয়নি। প্রতিটি query ধরে নেয়: `HELD` এবং `ExpiresAt ≤ এখন` মানে `EXPIRED`। তাই সিট ঠিক মেয়াদ শেষ হওয়ার মুহূর্ত থেকেই খালি গোনা হয়। একটি ছোট background কাজ (৫ সেকেন্ডে একবার) শুধু সংরক্ষিত status পরিষ্কার রাখে।

## API

brief-এর সব endpoint:

| Method & path | কাজ |
|---|---|
| `POST /classes` | ক্লাস তৈরি |
| `GET /classes`, `GET /classes/{classId}` | সিটের হিসাব |
| `POST /classes/{classId}/holds` | আবেদন (header `X-Student-Id`) |
| `GET /holds/{holdId}` | আবেদনের অবস্থা |
| `POST /holds/{holdId}/confirm` | ভর্তি নিশ্চিত (header) |
| `DELETE /holds/{holdId}` | বাতিল, `204` (header) |
| `GET /me/holds` | নিজের সব আবেদন (header) |

Error হলে body: `{ "code": "SEAT_FULL", "message": "…বাংলা বার্তা…" }`। `code` যন্ত্রের জন্য, `message` মানুষের জন্য।

## ফোল্ডার

```
backend/    .NET 8 API (src/Api) ও test (tests/Api.Tests), Dockerfile
frontend/   React + Vite, Dockerfile (nginx দিয়ে চলে)
docker-compose.yml, README.md, ASSUMPTIONS.md, AI_USAGE.md
```

Brief-এ অস্পষ্ট জায়গায় কী ধরা হয়েছে তা `ASSUMPTIONS.md`-এ, AI ব্যবহারের বিবরণ `AI_USAGE.md`-এ।
