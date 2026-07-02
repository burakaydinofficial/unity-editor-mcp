// Retry a function across a transient failure window — used to ride out the seconds where the editor is mid-domain-
// reload and the bridge is briefly gone. Returns fn()'s first non-throwing result; throws the last error at timeout.
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

export async function retry(fn, { timeoutMs = 60000, intervalMs = 500, onRetry } = {}) {
  const deadline = Date.now() + timeoutMs;
  let lastErr;
  for (;;) {
    try { return await fn(); }
    catch (err) {
      lastErr = err;
      if (Date.now() >= deadline) throw lastErr;
      if (onRetry) onRetry(err);
      await sleep(intervalMs);
    }
  }
}
