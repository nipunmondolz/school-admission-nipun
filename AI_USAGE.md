# AI_USAGE

## কোন AI tool

Claude (Anthropic) — chat ও কোড-লেখার সহায়ক হিসেবে।

ChatGPT (OpenAI) — ত্রুটি অনুসন্ধান, configuration সংশোধন, UI styling এবং পরীক্ষার ফল যাচাইয়ে সহায়তা।

## project-এর কোন অংশে

- **নকশা:** assignment-এর শর্তগুলো আগে ছোট ছোট নিয়মে ভেঙে নেওয়া (সীমিত সিট, মেয়াদি hold, idempotent confirm, restart-এও data টেকা) এবং প্রতিটির জন্য পরিচিত পদ্ধতি বাছা (row lock, lease/TTL, state machine, database constraint)।
- **Backend** (`backend/src`): API, locking, expiry, validation।
- **Test** (`backend/tests`): concurrency, দুই-instance, idempotency, expiry, invalid input।
- **Frontend** (`frontend`): UI, polling, countdown।
- **Docker ও নথি:** `docker-compose.yml`, Dockerfile, README, ASSUMPTIONS।

## AI কোথায় ভুল করেছিল এবং কীভাবে ধরা/সংশোধন হয়েছে

AI-এর লেখা কোড পর্যালোচনায় এই ভুলগুলো পাওয়া গিয়েছিল ও ঠিক করা হয়েছে:

1. **Test-এ নামের সংঘর্ষ:** test-এর helper method-এর নাম `HoldStatus` ছিল, যা app-এর `HoldStatus` ক্লাসের নামের সঙ্গে সংঘর্ষ করছিল। নাম বদলে `GetStatus` করা হয়েছে।
2. **Test-এ using বাদ:** `ConfigureTestServices` ব্যবহার করা হয়েছিল কিন্তু `Microsoft.AspNetCore.TestHost` namespace যোগ করা হয়নি। যোগ করা হয়েছে।
3. **অতি-ভঙ্গুর test case:** non-ASCII অঙ্ক (আরবি-ভারতীয় সংখ্যা) HTTP header-এ পাঠানোর test রাখা হয়েছিল, যা server-এর নয় বরং HTTP client-এর সীমাবদ্ধতায় ব্যর্থ হতে পারত। test থেকে সরানো হয়েছে (server কোড শুধু `'0'..'9'` গ্রহণ করে, তা অপরিবর্তিত)।

## ChatGPT-এর সহায়তায় স্থানীয় যাচাই ও সংশোধন

- প্রথম test run-এ ১৫টির মধ্যে ১৩টি পাস এবং ২টি concurrency test ব্যর্থ হয়। PostgreSQL log-এ `too many clients already` পাওয়া যায়।
- ChatGPT-এর সহায়তায় কারণ পর্যালোচনা করে `docker-compose.yml`-এ `backend`, `backend2` ও `tests`-এর connection string-এ `Maximum Pool Size=10` যোগ করেছি।
- একই tests আবার চালানোর পর **১৫টি পাস, ০টি ব্যর্থ** হয়েছে।
- ChatGPT-এর সহায়তায় সাধারণ CSS দিয়ে UI-এর রং ও spacing পরিবর্তন করেছি। নিজে UI থেকে confirmation, cancellation ও expiry এবং restart-এর পর তথ্য সংরক্ষণ যাচাই করেছি।