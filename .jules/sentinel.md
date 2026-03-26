## 2025-02-17 - [Fix Weak Random Number Generation for Security Purposes]
**Vulnerability:** Authorization PINs and fallback passwords were mathematically predictable because they used `Random.Shared.Next` and `Guid.NewGuid()`. Both rely on non-cryptographic pseudo-random number generators (PRNG).
**Learning:** Even short, temporary authentication vectors (like 6-digit PINs or 10-char passwords) must use full-entropy cryptographic RNG. The risk of brute-forcing or state-reconstruction is too high if they control identity linkage.
**Prevention:** Always use `System.Security.Cryptography.RandomNumberGenerator` for any token, PIN, or password generation.
