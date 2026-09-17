import json
import threading
import time
import unittest
import urllib.request
import metrics_agent as m

class RecoveryTests(unittest.TestCase):
    def test_health_does_not_call_hardware_provider(self):
        called=[]
        def sample():
            called.append(True)
            raise AssertionError("health must not sample hardware")
        server=m.create_server("127.0.0.1",0,snapshot_provider=sample)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{server.server_port}/health",timeout=1) as response:
                self.assertEqual(json.load(response),{"status":"ok","service":"turzx-metrics"})
            self.assertEqual(called,[])
        finally:
            server.shutdown();server.server_close();thread.join(1)
    def test_slow_gpu_cannot_queue_unbounded_reads(self):
        entered=threading.Event();release=threading.Event();calls=[];clock=[10.0]
        def sample():
            calls.append(1);entered.set();release.wait(2)
            return {"source":"nvml","usage_percent":50,"status":"ok"}
        publisher=m.GpuSnapshotPublisher(sample,now=lambda:clock[0])
        try:
            initial=publisher.read();self.assertEqual(initial["status"],"warming")
            self.assertTrue(entered.wait(1))
            start=time.monotonic()
            for _ in range(25):publisher.read()
            self.assertLess(time.monotonic()-start,.2);self.assertEqual(len(calls),1)
        finally:release.set()
        end=time.monotonic()+1
        while publisher._running and time.monotonic()<end:time.sleep(.01)
        self.assertEqual(publisher.read()["usage_percent"],50)
        clock[0]=11.5
        self.assertIn("stale",publisher.read()["source"])
    def test_occupied_listener_cannot_be_replaced(self):
        first=m.create_server("127.0.0.1",0,snapshot_provider=lambda:{})
        try:
            with self.assertRaises(OSError):m.create_server("127.0.0.1",first.server_port,snapshot_provider=lambda:{})
        finally:first.server_close()
if __name__=="__main__":unittest.main()
