// k6 load test for the notes API: https://grafana.com/docs/k6/latest/
//   k6 run loadtest/notes.js
//   docker run --rm -i --network host grafana/k6:2.4.0 run - < loadtest/notes.js
// Settings come from environment variables (k6 run -e NAME=value, or docker run -e NAME=value):
//   BASE_URL   the API, http://localhost:8080 by default
//   VUS        concurrent virtual users, 20 by default
//   DURATION   how long to keep the load up, 30s by default
//   TOKEN      a bearer access token, or
//   API_KEY    an API key with the notes:read and notes:write scopes.
//              Without either, only the health check is measured.
// Turn rate limiting off for the API under test (RATE_LIMITING_ENABLED=false in compose), or the
// limit, not the API, is what gets measured. See docs/load-testing.md.
import http from 'k6/http';
import { check, group } from 'k6';

const baseUrl = __ENV.BASE_URL || 'http://localhost:8080';
const auth = __ENV.TOKEN
  ? { Authorization: `Bearer ${__ENV.TOKEN}` }
  : __ENV.API_KEY ? { 'X-Api-Key': __ENV.API_KEY } : null;
const headers = { 'Content-Type': 'application/json', ...(auth || {}) };

export const options = {
  scenarios: {
    notes: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 20),
      duration: __ENV.DURATION || '30s',
    },
  },
  // The run fails when these are not met, so CI fails on a regression.
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500'],
    checks: ['rate>0.99'],
  },
};

export function setup() {
  const ready = http.get(`${baseUrl}/health/ready`);
  if (ready.status !== 200) {
    throw new Error(`${baseUrl}/health/ready answered ${ready.status}; start the API first.`);
  }
  if (!auth) {
    console.warn('No TOKEN or API_KEY: measuring the health check only.');
  }
}

export default function () {
  if (!auth) {
    check(http.get(`${baseUrl}/health/ready`), { 'ready answers 200': (r) => r.status === 200 });
    return;
  }

  group('create, read and list notes', () => {
    const title = `Load ${__VU}-${__ITER}-${Date.now()}`;
    const created = http.post(`${baseUrl}/v1/notes`, JSON.stringify({ title, content: 'k6' }), { headers, tags: { name: 'POST /v1/notes' } });
    check(created, { 'create answers 201': (r) => r.status === 201 });
    if (created.status !== 201) {
      return;
    }

    const id = created.json('id');
    const read = http.get(`${baseUrl}/v1/notes/${id}`, { headers, tags: { name: 'GET /v1/notes/{id}' } });
    check(read, { 'read answers 200': (r) => r.status === 200 });

    const list = http.get(`${baseUrl}/v1/notes?pageSize=20`, { headers, tags: { name: 'GET /v1/notes' } });
    check(list, { 'list answers 200': (r) => r.status === 200 });
  });
}
