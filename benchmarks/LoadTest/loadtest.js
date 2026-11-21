// k6 load test: https://grafana.com/docs/k6/latest/
//   k6 run -e BASE_URL=http://localhost:8080 benchmarks/LoadTest/loadtest.js
// Run it once against MiniNetHello and once against KestrelHello, on the same machine, with the same settings.
import http from "k6/http";
import { check } from "k6";

const BASE_URL = __ENV.BASE_URL || "http://localhost:8080";

export const options = {
  scenarios: {
    steady: {
      executor: "constant-vus",
      vus: Number(__ENV.VUS || 50),
      duration: __ENV.DURATION || "30s",
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.01"],
  },
};

export default function () {
  const text = http.get(`${BASE_URL}/hello/k6`);
  check(text, { "hello 200": (r) => r.status === 200 });

  const json = http.get(`${BASE_URL}/json`);
  check(json, { "json 200": (r) => r.status === 200 });
}
